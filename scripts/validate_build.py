#!/usr/bin/env python3
"""Check a release build locally or in CI without fetching/publishing the catalog."""
import argparse
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

from manifest import generate


def expected_jellyfin_abi(csproj):
    # Read independently of the manifest helper so its parsing is also checked.
    references = list(ET.parse(csproj).getroot().iter('PackageReference'))
    versions = []
    for name in ('Jellyfin.Controller', 'Jellyfin.Model'):
        matches = [item.get('Version') for item in references if item.get('Include') == name]
        if len(matches) != 1 or not re.fullmatch(r'\d+\.\d+\.\d+', matches[0] or ''):
            raise ValueError(f'Expected exactly one {name} reference with a stable three-part version')
        versions.append(matches[0])
    if versions[0] != versions[1]:
        raise ValueError('Jellyfin.Controller and Jellyfin.Model versions must match')
    return f'{versions[0]}.0'


def validate(configuration, version):
    project = Path(__file__).resolve().parents[1] / 'Jellyfin.Plugin.MetaTube'
    csproj = project / 'Jellyfin.Plugin.MetaTube.csproj'
    platform = {'Release': 'Jellyfin', 'Release.Emby': 'Emby'}[configuration]
    # Ask MSBuild for evaluated paths instead of duplicating framework/output settings.
    properties = json.loads(subprocess.check_output([
        'dotnet', 'msbuild', str(csproj),
        f'-property:Configuration={configuration}', f'-property:Version={version}',
        '-getProperty:TargetPath,BaseOutputPath',
    ], cwd=project, text=True))['Properties']
    archive_path = project / properties['BaseOutputPath'] / f'{platform}.MetaTube@v{version}.zip'
    assembly_path = project / properties['TargetPath']

    with ZipFile(archive_path) as archive:
        if archive.namelist() != ['MetaTube.dll']:
            raise ValueError(f'{archive_path.name} must contain only root MetaTube.dll')
        # Reading also verifies the entry CRC. Compare against this build's DLL.
        if archive.read('MetaTube.dll') != assembly_path.read_bytes():
            raise ValueError(f'{archive_path.name} differs from the built assembly')

    if platform == 'Jellyfin':
        target_abi = expected_jellyfin_abi(csproj)
        # Exercise the release helper directly; main() fetches the live catalog.
        entry = generate(str(archive_path), version, str(csproj))
        expected = {
            'targetAbi': target_abi,
            'version': version,
            'checksum': hashlib.md5(archive_path.read_bytes()).hexdigest(),
            'sourceUrl': (
                'https://github.com/metatube-community/jellyfin-plugin-metatube/'
                f'releases/download/v{version}/{archive_path.name}'
            ),
        }
        for key, value in expected.items():
            if entry.get(key) != value:
                raise ValueError(f'Manifest {key}: expected {value!r}, got {entry.get(key)!r}')

    print(f'Validated {archive_path.name}' +
          (' and Jellyfin manifest' if platform == 'Jellyfin' else ''))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('configuration', choices=('Release', 'Release.Emby'))
    parser.add_argument('version', help='The Version property passed to dotnet build')
    args = parser.parse_args()
    validate(args.configuration, args.version)
