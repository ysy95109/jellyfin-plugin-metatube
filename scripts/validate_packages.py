#!/usr/bin/env python3
"""Inspect both ZIPs and verify their hash-bound build evidence before promotion."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen
from manifest import validate_archive


def validate(directory):
    evidence = json.loads((directory / 'build-evidence.json').read_text(encoding='utf-8'))
    for platform in ('Jellyfin', 'Emby'):
        path = directory / f'{platform}.MetaTube@v{evidence["version"]}.zip'
        validate_archive(path, evidence['version'], platform)
        if hashlib.sha256(path.read_bytes()).hexdigest() != evidence['sha256'][platform]:
            raise ValueError(f'{platform} artifact differs from build evidence')
    return evidence


def verify_downloads(directory, repository):
    import re
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Expected a GitHub owner/repository')
    evidence = validate(directory)
    for platform in ('Jellyfin', 'Emby'):
        name = f'{platform}.MetaTube@v{evidence["version"]}.zip'
        url = f'https://github.com/{repository}/releases/download/v{evidence["version"]}/{name}'
        with urlopen(url, timeout=60) as response:
            if hashlib.sha256(response.read()).hexdigest() != evidence['sha256'][platform]:
                raise ValueError(f'Published {platform} download differs from the tested artifact')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--verify-downloads', metavar='OWNER/REPOSITORY')
    args = parser.parse_args()
    print(json.dumps(validate(args.directory), indent=2))
    if args.verify_downloads:
        verify_downloads(args.directory, args.verify_downloads)
