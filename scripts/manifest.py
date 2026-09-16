#!/usr/bin/env python3
"""Validate a release package and append it to the existing plugin catalog."""
import argparse
import copy
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from urllib.request import urlopen
from zipfile import ZipFile

GUID = '01cc53ec-c415-4108-bbd4-a684a9801a32'
REPOSITORY = 'metatube-community/jellyfin-plugin-metatube'
PROJECT = Path(__file__).resolve().parents[1] / 'Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj'


def version_tuple(value, count=4):
    if not isinstance(value, str) or not re.fullmatch(r'\d+(?:\.\d+){' + str(count - 1) + '}', value):
        raise ValueError(f'Expected a stable {count}-part version: {value!r}')
    parts = tuple(map(int, value.split('.')))
    if any(part > 65534 for part in parts) or '.'.join(map(str, parts)) != value:
        raise ValueError(f'Invalid assembly version: {value!r}')
    return parts


def get_jellyfin_version(csproj):
    packages = {name: [] for name in ('Jellyfin.Controller', 'Jellyfin.Model')}
    for package in ET.parse(csproj).getroot().iter('PackageReference'):
        if package.get('Include') in packages:
            packages[package.get('Include')].append(package.get('Version'))
    controller, model = packages.values()
    if len(controller) != 1 or controller != model:
        raise ValueError('Exactly one matching Controller/Model package version is required')
    version_tuple(controller[0], 3)
    return controller[0]


def md5sum(filename):
    return hashlib.md5(Path(filename).read_bytes()).hexdigest()


def validate_archive(filename, version, platform='Jellyfin'):
    version_tuple(version)
    if platform not in ('Jellyfin', 'Emby'):
        raise ValueError('Unknown platform')
    if Path(filename).name != f'{platform}.MetaTube@v{version}.zip':
        raise ValueError('Archive name must match platform and release version')
    with ZipFile(filename) as archive:
        if archive.namelist() != ['MetaTube.dll'] or archive.testzip() is not None:
            raise ValueError('Archive must contain only a valid root MetaTube.dll')
        if not archive.read('MetaTube.dll').startswith(b'MZ'):
            raise ValueError('MetaTube.dll is not a PE assembly')


def generate(filename, version, csproj=PROJECT, repository=REPOSITORY):
    validate_archive(filename, version)
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Expected a GitHub owner/repository')
    return {
        'checksum': md5sum(filename),
        'changelog': 'Jellyfin 12.0 (.NET 10) compatibility; Emby support retained.',
        'targetAbi': f'{get_jellyfin_version(csproj)}.0',
        'sourceUrl': f'https://github.com/{repository}/releases/download/v{version}/Jellyfin.MetaTube@v{version}.zip',
        'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'version': version,
    }


def update_manifest(manifest, entry):
    result = copy.deepcopy(manifest)
    if not isinstance(result, list):
        raise ValueError('Catalog must be a list')
    matches = [package for package in result if package.get('guid', '').lower() == GUID]
    if len(matches) != 1 or matches[0].get('name') != 'MetaTube':
        raise ValueError('Catalog must contain exactly one MetaTube entry with the expected GUID')
    versions = matches[0].get('versions')
    if not isinstance(versions, list):
        raise ValueError('Catalog versions must be a list')
    existing = [item for item in versions if item.get('version') == entry['version']]
    if existing:
        if len(existing) != 1 or any(existing[0].get(key) != entry[key]
                                     for key in ('checksum', 'targetAbi', 'sourceUrl')):
            raise ValueError('Release version already exists with different content')
        return result  # Preserve original timestamp and changelog on retry.
    if versions and version_tuple(entry['version']) <= max(version_tuple(item['version']) for item in versions):
        raise ValueError('New release version must be newer than the existing catalog')
    versions.insert(0, entry)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    parser.add_argument('--repository', default=REPOSITORY)
    parser.add_argument('--base-manifest', type=Path)
    parser.add_argument('--output', type=Path, default=Path('manifest.json'))
    parser.add_argument('--verify-download', action='store_true')
    args = parser.parse_args()
    match = re.fullmatch(r'Jellyfin\.MetaTube@v(.+)\.zip', args.archive.name)
    if not match:
        parser.error('Expected Jellyfin.MetaTube@vVERSION.zip')
    entry = generate(args.archive, match[1], repository=args.repository)
    if args.base_manifest:
        manifest = json.loads(args.base_manifest.read_text(encoding='utf-8'))
    else:
        with urlopen(f'https://raw.githubusercontent.com/{args.repository}/dist/manifest.json', timeout=30) as response:
            manifest = json.load(response)
    result = update_manifest(manifest, entry)
    if args.verify_download:
        with urlopen(entry['sourceUrl'], timeout=60) as response:
            if hashlib.md5(response.read()).hexdigest() != entry['checksum']:
                raise ValueError('Published download does not match the validated archive')
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
