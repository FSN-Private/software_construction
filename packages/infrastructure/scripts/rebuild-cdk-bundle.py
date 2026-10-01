"""Rebuild or verify the owner-approved CDK bundle using verified npm archives."""
import argparse
import base64
import copy
import gzip
import hashlib
import json
import tarfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('cdk', type=Path)
parser.add_argument('replacement', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--verify-only', action='store_true')
args = parser.parse_args()
policy = json.loads((Path(__file__).parent.parent / 'vendor/cdk-patch-provenance.json').read_text())

for path, expected in [(args.cdk, policy['upstream']), (args.replacement, policy['replacement'])]:
    actual = 'sha512-' + base64.b64encode(hashlib.sha512(path.read_bytes()).digest()).decode()
    if actual != expected['integrity']:
        raise ValueError(f'Input integrity mismatch: {path}')

prefix = policy['subtree']
if not args.verify_only:
    if args.output.resolve() in [args.cdk.resolve(), args.replacement.resolve()]:
        raise ValueError('Output must not overwrite an input archive')
    with tarfile.open(args.cdk, 'r:gz') as cdk, tarfile.open(args.replacement, 'r:gz') as patch:
        with args.output.open('wb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode='w', format=tarfile.PAX_FORMAT) as archive:
                for source, replacing in [(cdk, False), (patch, True)]:
                    for member in source.getmembers():
                        if not replacing and (member.name == prefix.rstrip('/') or member.name.startswith(prefix)):
                            continue
                        info = copy.copy(member)
                        if replacing:
                            if not info.name.startswith('package/'):
                                raise ValueError('Unexpected patch archive path')
                            info.name = prefix + info.name.removeprefix('package/')
                        info.mtime = 0
                        info.uid = info.gid = 0
                        info.uname = info.gname = ''
                        info.pax_headers = {}
                        archive.addfile(info, source.extractfile(member) if member.isfile() else None)

def files(path):
    with tarfile.open(path, 'r:gz') as archive:
        return {member.name: hashlib.sha256(archive.extractfile(member).read()).hexdigest()
                for member in archive.getmembers() if member.isfile()}

original, patched, replacement = files(args.cdk), files(args.output), files(args.replacement)
if {name: digest for name, digest in original.items() if not name.startswith(prefix)} != {
        name: digest for name, digest in patched.items() if not name.startswith(prefix)}:
    raise ValueError('CDK content outside the replacement subtree changed')
if {prefix + name.removeprefix('package/'): digest for name, digest in replacement.items()} != {
        name: digest for name, digest in patched.items() if name.startswith(prefix)}:
    raise ValueError('Replacement content does not match the verified npm package')
if hashlib.sha512(args.output.read_bytes()).hexdigest() != policy['sha512']:
    raise ValueError('Output archive differs from the approved digest; review compressor/toolchain differences before adoption')
print('Verified archive integrity and exact replacement; every other CDK file is unchanged.')
