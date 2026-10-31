"""Native integration/soak runner. Synthetic sources by default; --hardware explicitly selects real devices.
Examples: python scripts/exercise_media.py --seconds 60
          python scripts/exercise_media.py --seconds 28800 --part-seconds 3600
          python scripts/exercise_media.py --hardware --seconds 20
          python scripts/exercise_media.py --crash-after 12
"""
import argparse, ctypes as c, json, os, pathlib, time

class Config(c.Structure):
    _fields_ = [(x, c.c_char_p) for x in ('camera','microphone','caps','output')] + [(x,c.c_int) for x in ('pre','video_kbps','audio_kbps','channels','part','test')]
class Event(c.Structure):
    _fields_ = [('type',c.c_int),('first',c.c_double),('second',c.c_double),('time',c.c_int64),('duration',c.c_int64),('text',c.c_char*2048)]
class Memory(c.Structure):
    _fields_ = [('cb',c.c_ulong),('faults',c.c_ulong)] + [(name,c.c_size_t) for name in ('peak_working','working','peak_paged','paged','peak_nonpaged','nonpaged','pagefile','peak_pagefile','private')]

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--seconds',type=float,default=30); parser.add_argument('--hardware',action='store_true')
    parser.add_argument('--part-seconds',type=int,default=10); parser.add_argument('--crash-after',type=float); parser.add_argument('--output')
    parser.add_argument('--package',type=pathlib.Path,help='Test the shipped native adapter and reduced runtime with an isolated search path')
    parser.add_argument('--test-plugins',type=pathlib.Path,help='Directory containing only synthetic source plugins (not shipped)')
    args=parser.parse_args()
    root=pathlib.Path(__file__).resolve().parent.parent
    package=args.package.resolve() if args.package else None
    gst=package/'gstreamer' if package else root/'.tools/gstreamer'
    out=pathlib.Path(args.output).resolve() if args.output else root/'artifacts'/('hardware-' if args.hardware else 'synthetic-')/time.strftime('%Y%m%d-%H%M%S')
    out.mkdir(parents=True,exist_ok=True)
    if package:
        os.environ['PATH']=str(package)+';'+os.environ['SystemRoot']+'\\System32;'+os.environ['SystemRoot']
        os.environ['GST_REGISTRY_1_0']=str(out/'registry.bin')
        app_directory=os.add_dll_directory(str(package))
    dll_directory=os.add_dll_directory(str(gst/'bin'))
    os.environ['PATH']=str(gst/'bin')+';'+os.environ.get('PATH','')
    os.environ['GST_PLUGIN_PATH_1_0']=str(gst/'lib/gstreamer-1.0')
    if args.test_plugins:
        os.environ['GST_PLUGIN_PATH_1_0']+=';'+str(args.test_plugins.resolve())
    os.environ['GST_PLUGIN_SYSTEM_PATH_1_0']=str(gst/'lib/gstreamer-1.0')
    os.environ['GST_PLUGIN_SCANNER']=str(gst/'libexec/gstreamer-1.0/gst-plugin-scanner.exe')
    lib=c.CDLL(str(package/'HiddenRecorder.Media.dll' if package else root/'build/native/Release/HiddenRecorder.Media.dll'))
    lib.hr_create.restype=c.c_void_p
    lib.hr_start.argtypes=[c.c_void_p,c.POINTER(Config)]; lib.hr_begin.argtypes=[c.c_void_p,c.c_int64]
    lib.hr_poll.argtypes=[c.c_void_p,c.POINTER(Event)]; lib.hr_finish.argtypes=[c.c_void_p]
    lib.hr_stop.argtypes=[c.c_void_p]; lib.hr_destroy.argtypes=[c.c_void_p]; lib.hr_last_error.restype=c.c_char_p
    def check(value):
        if not value: raise RuntimeError(lib.hr_last_error().decode())
    camera=mic=b''; caps=b'video/x-raw,width=640,height=360,framerate=30/1'
    if args.hardware:
        buffer=c.create_string_buffer(1024*1024); check(lib.hr_catalog(buffer,len(buffer))); catalog=json.loads(buffer.value)
        if not catalog['cameras'] or not catalog['microphones']: raise RuntimeError('Camera/microphone unavailable')
        device=catalog['cameras'][0]; mode=next((m for m in device['modes'] if m['width']==1920 and m['height']==1080 and m['fpsNumerator']==30),device['modes'][0])
        camera=device['id'].encode(); mic=catalog['microphones'][0]['id'].encode(); caps=mode['caps'].encode()
        print('Using real devices:', device['name'], catalog['microphones'][0]['name'], flush=True)
    config=Config(camera,mic,caps,str(out).encode(),5,6000 if args.hardware else 1000,128,1,args.part_seconds,0 if args.hardware else 1)
    engine=lib.hr_create(); check(engine); began=False; ready=False; latest=0; files=0
    kernel=c.WinDLL('kernel32'); kernel.GetCurrentProcess.restype=c.c_void_p
    psapi=c.WinDLL('psapi'); psapi.GetProcessMemoryInfo.argtypes=[c.c_void_p,c.POINTER(Memory),c.c_ulong]
    resource_log=(out/'resources.jsonl').open('w',encoding='utf-8')
    try:
        check(lib.hr_start(engine,c.byref(config))); started=time.monotonic(); next_resource=0
        while time.monotonic()-started < args.seconds:
            event=Event()
            while lib.hr_poll(engine,c.byref(event)):
                if event.type==6: raise RuntimeError(event.text.decode())
                if event.type==1: ready=True
                if event.type==2: latest=event.time
                if event.type==5: files+=1; print('Closed',pathlib.Path(event.text.decode()).name,flush=True)
            elapsed=time.monotonic()-started
            if elapsed>6 and ready and not began: check(lib.hr_begin(engine,latest)); began=True
            if elapsed>15 and not ready: raise RuntimeError('No capture readiness after 15 seconds')
            if elapsed>=next_resource:
                memory=Memory(); memory.cb=c.sizeof(memory); psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(),c.byref(memory),memory.cb)
                handles=c.c_ulong(); kernel.GetProcessHandleCount.argtypes=[c.c_void_p,c.POINTER(c.c_ulong)]; kernel.GetProcessHandleCount(kernel.GetCurrentProcess(),c.byref(handles))
                resource_log.write(json.dumps({'seconds':round(elapsed,1),'private_bytes':memory.private,'working_bytes':memory.working,'handles':handles.value})+'\n'); resource_log.flush(); next_resource=elapsed+10
            if args.crash_after and elapsed>=args.crash_after: os._exit(77)
            time.sleep(.01)
        check(lib.hr_finish(engine)); check(lib.hr_stop(engine))
        if not began: raise RuntimeError('Recording was never started')
        print('PASS',out,flush=True)
    finally:
        lib.hr_destroy(engine); resource_log.close(); dll_directory.close()
        if package: app_directory.close()

if __name__=='__main__': main()
