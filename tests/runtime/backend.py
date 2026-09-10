#!/usr/bin/env python3
"""Loopback-only synthetic backend and staging catalog for manual server tests.

Usage: python tests/runtime/backend.py --package PATH_TO_ZIP --port 18197
No external metadata sources or credentials are used. Stop with Ctrl+C.
"""
import argparse
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--package', required=True, type=Path)
parser.add_argument('--port', type=int, default=18197)
parser.add_argument('--image', required=True, type=Path)
parser.add_argument('--trailer', required=True, type=Path)
args = parser.parse_args()
base = f'http://127.0.0.1:{args.port}'
package = args.package.read_bytes()
version = args.package.name.split('@v')[1].removesuffix('.zip')
movie = dict(provider='Fixture', id='m1', number='TEST-001', title='Synthetic movie',
             summary='Local migration test', release_date='2025-01-02T00:00:00Z', score=4.2,
             director='Synthetic Director', actors=['Synthetic Actor'], genres=['Genre 10', 'Genre 2'],
             maker='Synthetic Studio', label='Synthetic Label', series='Synthetic Series',
             preview_images=[base + '/image.png'], preview_video_url=base + '/trailer.mp4',
             thumb_url=base + '/image.png')
actor = dict(provider='Fixture', id='a1', name='Synthetic Actor', aliases=['Synthetic Alias'],
             nationality='Japan', birthday='1990-01-02T00:00:00Z', images=[base + '/image.png'])


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = urlparse(self.path).path
        content_type = 'application/json'
        if path == '/manifest.json':
            payload = [dict(guid='01cc53ec-c415-4108-bbd4-a684a9801a32', name='MetaTube',
                            description='Isolated migration test', overview='Synthetic runtime test',
                            owner='MetaTube', category='Metadata', versions=[dict(
                                version=version, targetAbi='12.0.0.0', checksum=hashlib.md5(package).hexdigest(),
                                sourceUrl=base + '/plugin.zip', timestamp='2026-09-10T00:00:00Z', changelog='Runtime candidate')])]
        elif path == '/plugin.zip':
            payload, content_type = package, 'application/zip'
        elif path == '/image.png' or path.startswith('/v1/images/'):
            payload, content_type = args.image.read_bytes(), 'image/png'
        elif path == '/trailer.mp4':
            payload, content_type = args.trailer.read_bytes(), 'video/mp4'
        elif path.startswith('/v1/'):
            if path == '/v1/translate':
                data = dict(translated_text='Translated synthetic title', to='en', **{'from': 'ja'})
            else:
                data = actor if '/actors' in path else movie
                if path.endswith('/search'):
                    data = [] if parse_qs(urlparse(self.path).query).get('q') == ['missing'] else [data]
            payload = dict(data=data)
        else:
            self.send_error(404)
            return
        if not isinstance(payload, bytes):
            payload = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


print(f'Synthetic backend and catalog: {base}/manifest.json', flush=True)
ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
