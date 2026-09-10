import copy
import io
import hashlib
import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from manifest import GUID, generate, get_jellyfin_version, update_manifest, validate_archive, version_tuple
from validate_packages import validate, verify_downloads


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.version = '2026.910.1200.0'
        self.archive = self.root / f'Jellyfin.MetaTube@v{self.version}.zip'
        with ZipFile(self.archive, 'w') as archive:
            archive.writestr('MetaTube.dll', b'MZ synthetic assembly')
        self.project = self.root / 'plugin.csproj'
        self.project.write_text('<Project><ItemGroup><PackageReference Include="Jellyfin.Controller" Version="12.0.0"/>'
                                '<PackageReference Include="Jellyfin.Model" Version="12.0.0"/></ItemGroup></Project>')
        self.old = {'version': '2025.1102.2200.0', 'targetAbi': '10.11.0.0', 'checksum': 'old', 'sourceUrl': 'old'}
        self.catalog = [{'name': 'Unrelated', 'guid': 'other', 'versions': []},
                        {'name': 'MetaTube', 'guid': GUID, 'versions': [self.old]}]

    def entry(self):
        return generate(self.archive, self.version, self.project)

    def test_generated_abi_hash_url_and_utc_timestamp(self):
        entry = self.entry()
        self.assertEqual('12.0.0.0', entry['targetAbi'])
        self.assertEqual(hashlib.md5(self.archive.read_bytes()).hexdigest(), entry['checksum'])
        self.assertTrue(entry['sourceUrl'].endswith(f'/v{self.version}/{self.archive.name}'))
        parsed = datetime.strptime(entry['timestamp'], '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=timezone.utc)
        self.assertLess(abs((datetime.now(timezone.utc) - parsed).total_seconds()), 5)

    def test_preserves_history_and_old_server_selection(self):
        catalog = update_manifest(self.catalog, self.entry())
        self.assertEqual(self.catalog[0], catalog[0])
        self.assertEqual(self.old, catalog[1]['versions'][1])
        self.assertEqual([self.old], self.catalog[1]['versions'])
        eligible = [v for v in catalog[1]['versions'] if version_tuple(v['targetAbi']) <= version_tuple('10.11.11.0')]
        self.assertEqual([self.old], eligible)
        eligible = [v for v in catalog[1]['versions'] if version_tuple(v['targetAbi']) <= version_tuple('12.0.0.0')]
        self.assertEqual(self.version, eligible[0]['version'])

    def test_idempotent_retry_and_conflicting_version(self):
        entry = self.entry()
        catalog = update_manifest(self.catalog, entry)
        later = dict(entry, timestamp='2026-09-11T00:00:00Z')
        self.assertEqual(catalog, update_manifest(catalog, later))
        with self.assertRaises(ValueError):
            update_manifest(catalog, dict(entry, checksum='different'))

    def test_rejects_missing_mismatched_and_prerelease_dependencies(self):
        for model in ('12.0.1', '12.0.0-rc1', ''):
            with self.subTest(model=model):
                self.project.write_text(f'<Project><PackageReference Include="Jellyfin.Controller" Version="12.0.0"/>'
                                        f'<PackageReference Include="Jellyfin.Model" Version="{model}"/></Project>')
                with self.assertRaises(ValueError):
                    get_jellyfin_version(self.project)
        self.project.write_text('<Project><PackageReference Include="Jellyfin.Controller" Version="12.0.0-rc1"/>'
                                '<PackageReference Include="Jellyfin.Model" Version="12.0.0-rc1"/></Project>')
        with self.assertRaises(ValueError):
            get_jellyfin_version(self.project)

    def test_rejects_wrong_identity_and_duplicate_packages(self):
        wrong = copy.deepcopy(self.catalog)
        wrong[1]['guid'] = 'unexpected'
        with self.assertRaises(ValueError):
            update_manifest(wrong, self.entry())
        with self.assertRaises(ValueError):
            update_manifest(self.catalog + [self.catalog[1]], self.entry())

    def test_rejects_bad_versions_repository_and_archive_contents(self):
        for version in ('12.0', '1.2.3.4-rc1', '1.2.3.70000', '../1.2.3.4'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                version_tuple(version)
        with self.assertRaises(ValueError):
            generate(self.archive, self.version, self.project, 'https://wrong.invalid')
        with self.assertRaises(ValueError):
            validate_archive(self.archive, '2026.910.1300.0')
        with ZipFile(self.archive, 'a') as archive:
            archive.writestr('MediaBrowser.Controller.dll', b'MZ')
        with self.assertRaises(ValueError):
            self.entry()

    def test_promotion_rejects_modified_zip(self):
        emby = self.root / f'Emby.MetaTube@v{self.version}.zip'
        emby.write_bytes(self.archive.read_bytes())
        hashes = {platform: hashlib.sha256(path.read_bytes()).hexdigest()
                  for platform, path in [('Jellyfin', self.archive), ('Emby', emby)]}
        (self.root / 'build-evidence.json').write_text(json.dumps({'version': self.version, 'sha256': hashes}))
        validate(self.root)
        with ZipFile(emby, 'w') as archive:
            archive.writestr('MetaTube.dll', b'MZ modified')
        with self.assertRaises(ValueError):
            validate(self.root)

    def test_promotion_checks_both_published_downloads(self):
        emby = self.root / f'Emby.MetaTube@v{self.version}.zip'
        emby.write_bytes(self.archive.read_bytes())
        digest = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        (self.root / 'build-evidence.json').write_text(json.dumps({
            'version': self.version, 'sha256': {'Jellyfin': digest, 'Emby': digest}}))
        with patch('validate_packages.urlopen', side_effect=[io.BytesIO(self.archive.read_bytes()),
                                                             io.BytesIO(emby.read_bytes())]) as download:
            verify_downloads(self.root, 'owner/repository')
            self.assertEqual(2, download.call_count)
            self.assertIn('/Emby.MetaTube@v', download.call_args.args[0])
        with patch('validate_packages.urlopen', side_effect=[io.BytesIO(self.archive.read_bytes()),
                                                             io.BytesIO(b'changed Emby asset')]):
            with self.assertRaises(ValueError):
                verify_downloads(self.root, 'owner/repository')


if __name__ == '__main__':
    unittest.main()
