"""Read-only MSI extraction: never invokes Windows Installer installation actions."""
import ctypes as c
from ctypes import wintypes as w
import pathlib, subprocess, sys, tempfile

msi = c.WinDLL('msi')
msi.MsiOpenDatabaseW.argtypes = [w.LPCWSTR, w.LPCWSTR, c.POINTER(w.UINT)]
msi.MsiDatabaseOpenViewW.argtypes = [w.UINT, w.LPCWSTR, c.POINTER(w.UINT)]
msi.MsiViewExecute.argtypes = [w.UINT, w.UINT]
msi.MsiViewFetch.argtypes = [w.UINT, c.POINTER(w.UINT)]
msi.MsiRecordGetStringW.argtypes = [w.UINT, w.UINT, w.LPWSTR, c.POINTER(w.DWORD)]
msi.MsiRecordReadStream.argtypes = [w.UINT, w.UINT, c.c_void_p, c.POINTER(w.DWORD)]

def check(code):
    if code: raise RuntimeError(f'MSI read error {code}')

def extract(source, target):
    db = w.UINT()
    check(msi.MsiOpenDatabaseW(str(source.resolve()), None, c.byref(db)))
    def rows(sql, count):
        view = w.UINT(); check(msi.MsiDatabaseOpenViewW(db, sql, c.byref(view)))
        check(msi.MsiViewExecute(view, 0))
        try:
            while True:
                rec = w.UINT(); result = msi.MsiViewFetch(view, c.byref(rec))
                if result == 259: break
                check(result)
                try:
                    values = []
                    for index in range(1, count + 1):
                        buf = c.create_unicode_buffer(32768); size = w.DWORD(32768)
                        check(msi.MsiRecordGetStringW(rec, index, buf, c.byref(size)))
                        values.append(buf.value)
                    yield values, rec
                finally: msi.MsiCloseHandle(rec)
        finally: msi.MsiCloseHandle(view)
    try:
        dirs = {r[0]: (r[1], r[2].split(':')[0].split('|')[-1]) for r, _ in rows('SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`', 3)}
        components = {r[0]: r[1] for r, _ in rows('SELECT `Component`,`Directory_` FROM `Component`', 2)}
        def directory(key):
            parent, name = dirs[key]
            return (directory(parent) if parent else pathlib.Path()) / (name if name not in ('.', 'SourceDir') else '')
        files = {r[0]: directory(components[r[1]]) / r[2].split('|')[-1] for r, _ in rows('SELECT `File`,`Component_`,`FileName` FROM `File`', 3)}
        cabinets = [r[0][1:] for r, _ in rows('SELECT `Cabinet` FROM `Media`', 1) if r[0].startswith('#')]
        with tempfile.TemporaryDirectory(dir=target.parent) as tmp:
            tmp = pathlib.Path(tmp)
            for name in cabinets:
                escaped = name.replace("'", "''")
                for _, record in rows(f"SELECT `Data` FROM `_Streams` WHERE `Name`='{escaped}'", 0):
                    cabinet = tmp / 'payload.cab'
                    with cabinet.open('wb') as output:
                        while True:
                            data = c.create_string_buffer(1024 * 1024); size = w.DWORD(len(data))
                            check(msi.MsiRecordReadStream(record, 1, data, c.byref(size)))
                            if not size.value: break
                            output.write(data.raw[:size.value])
                    if cabinet.stat().st_size == 0:
                        continue  # Empty media cabinets exist in the SDK MSI.
                    unpacked = tmp / 'files'; unpacked.mkdir(exist_ok=True)
                    result = subprocess.run(['expand.exe', '-R', '-F:*', str(cabinet), str(unpacked)], capture_output=True)
                    if result.returncode: raise RuntimeError(result.stdout.decode(errors='replace'))
                    for file in unpacked.iterdir():
                        if file.name == 'payload.cab' and file.stat().st_size < 100:
                            file.unlink(); continue  # An empty CAB is copied verbatim by expand.exe.
                        if file.name not in files: raise RuntimeError(f'Unknown MSI file {file.name}, {file.stat().st_size} bytes, cabinet {name}, header {cabinet.read_bytes()[:24]!r}, expand {result.stdout[-2000:]!r}')
                        parts = files[file.name].parts
                        # MSI installation root is irrelevant; retain bin/lib/include/share subtrees.
                        start = next((i for i,p in enumerate(parts) if p in ('bin','lib','include','share','libexec','etc')), None)
                        relative = pathlib.Path(*parts[start:]) if start is not None else pathlib.Path('licenses') / file.name
                        destination = (target / relative).resolve()
                        if not destination.is_relative_to(target.resolve()): raise RuntimeError('Unsafe MSI path')
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        file.replace(destination)
                    cabinet.unlink()
    finally: msi.MsiCloseHandle(db)

if __name__ == '__main__':
    destination = pathlib.Path(sys.argv[2]); destination.mkdir(parents=True, exist_ok=True)
    extract(pathlib.Path(sys.argv[1]), destination)
