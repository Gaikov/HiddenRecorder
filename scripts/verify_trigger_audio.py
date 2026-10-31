"""Check actual decoded trigger sounds and pre-roll in the diagnostic 'verify' recording."""
import av
import json
import pathlib
import sys

folder = pathlib.Path(sys.argv[1])
report = json.loads((folder / 'integration.json').read_text(encoding='utf-8'))
assert not report['Hardware'], 'This check requires the deterministic synthetic scenario'
files = report['Files']
assert len(files) == 3
runs = []
with av.open(files[0]) as container:
    start = end = None
    for frame in container.decode(audio=0):
        loud = abs(frame.to_ndarray()).max() > .1
        if loud:
            if start is None: start = frame.time
            end = frame.time + frame.samples / frame.sample_rate
        elif start is not None:
            runs.append((start, end)); start = end = None
    if start is not None: runs.append((start, end))
assert len(runs) == 2, f'Expected two recorded sound pulses, found {runs}'
assert 4.8 <= runs[0][0] <= 6.2, f'Five seconds of pre-roll are missing: {runs}'
assert all(.2 <= end - start <= .5 for start, end in runs), f'Trigger sounds were clipped: {runs}'
assert 2.0 <= runs[1][0] - runs[0][0] <= 2.5, f'Pulse timing changed: {runs}'
print(json.dumps({'pass': True, 'recorded_pulses_seconds': runs, 'pre_roll_seconds': runs[0][0]}, indent=2))
