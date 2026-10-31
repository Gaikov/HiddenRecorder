"""Copy the selected plugins and their PE import/delay-import dependency closure.

Only Python's standard library is needed. Run after publishing the app and VC CRT.
Dynamic GStreamer element discovery is covered by PLUGINS, not by PE imports.
Keep integration checks in sync when changing this list or the pinned SDK.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import struct


PLUGINS = (
    'coreelements', 'app', 'typefindfunctions', 'playback', 'videoconvertscale',
    'videorate', 'audioconvert', 'audioresample', 'level', 'mediafoundation',
    'wasapi2', 'wasapi', 'd3d11', 'openh264', 'videoparsersbad', 'audioparsers',
    'isomp4', 'multifile', 'jpeg', 'autodetect',
)


def imports(path):
    """Read ordinary and delay-load imports from a PE32/PE32+ image."""
    data = path.read_bytes()
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError(f'Invalid PE image: {path}')
    sections = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    if magic not in (0x10b, 0x20b):
        raise ValueError(f'Unsupported PE image: {path}')
    directories = optional + (112 if magic == 0x20b else 96)
    image_base = struct.unpack_from('<Q' if magic == 0x20b else '<I', data,
                                    optional + (24 if magic == 0x20b else 28))[0]
    section_table = optional + optional_size

    def offset(rva):
        for index in range(sections):
            size, address, raw_size, raw = struct.unpack_from('<IIII', data, section_table + index * 40 + 8)
            if address <= rva < address + max(size, raw_size):
                return raw + rva - address
        raise ValueError(f'Unmapped RVA {rva:x} in {path}')

    result = set()
    for directory, stride, name_offset in ((1, 20, 12), (13, 32, 4)):
        rva, size = struct.unpack_from('<II', data, directories + directory * 8)
        if not rva:
            continue
        start = offset(rva)
        for entry in range(start, start + size, stride):
            if not any(data[entry:entry + stride]):
                break
            name_rva = struct.unpack_from('<I', data, entry + name_offset)[0]
            if directory == 13 and not (struct.unpack_from('<I', data, entry)[0] & 1):
                name_rva -= image_base
            name = offset(name_rva)
            result.add(data[name:data.index(b'\0', name)].decode('ascii').lower())
    return result


def package(source, destination):
    target = destination / 'gstreamer'
    libraries = {p.name.lower(): p for p in (source / 'bin').glob('*.dll')}
    seeds = [source / 'lib/gstreamer-1.0' / f'gst{name}.dll' for name in PLUGINS]
    scanner = source / 'libexec/gstreamer-1.0/gst-plugin-scanner.exe'
    pending = seeds + [scanner, destination / 'HiddenRecorder.Media.dll']
    required, visited, external = set(), set(), set()
    system = Path(os.environ['SystemRoot']) / 'System32'
    while pending:
        path = pending.pop()
        if path in visited:
            continue
        visited.add(path)
        for name in imports(path):
            if name in libraries:
                required.add(name)
                if libraries[name] not in visited:
                    pending.append(libraries[name])
            elif (destination / name).is_file() or (system / name).is_file() or name.startswith(('api-ms-', 'ext-ms-')):
                external.add(name)
            else:
                raise RuntimeError(f'Missing dependency {name} imported by {path.name}')
    for path in seeds + [scanner] + [libraries[name] for name in sorted(required)]:
        output = target / path.relative_to(source)
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, output)
    # Retain the upstream license collection, including dependency notices.
    shutil.copytree(source / 'share/licenses', target / 'share/licenses')
    manifest = {
        'plugins': list(PLUGINS), 'runtime_dlls': sorted(required),
        'platform_or_app_dependencies': sorted(external),
        'runtime_bytes': sum(libraries[name].stat().st_size for name in required),
        'excluded_runtime_bytes': sum(p.stat().st_size for name, p in libraries.items() if name not in required),
    }
    (target / 'dependencies.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'GStreamer: {len(required)}/{len(libraries)} runtime DLLs, {manifest["runtime_bytes"] / 2**20:.2f} MiB')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--destination', required=True, type=Path)
    args = parser.parse_args()
    package(args.source.resolve(), args.destination.resolve())
