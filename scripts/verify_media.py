"""Decode all frames and verify continuous timestamps. Requires PyAV (pip install av)."""
import argparse, av, json, pathlib, struct

def verify_regular_index(path):
    def boxes(file, end):
        while file.tell() < end:
            start = file.tell()
            size, kind = struct.unpack('>I4s', file.read(8))
            if size == 1: size = struct.unpack('>Q', file.read(8))[0]
            assert size >= file.tell() - start and start + size <= end, 'Incomplete MP4 box'
            yield kind, start + size
            file.seek(start + size)
    with path.open('rb') as file:
        indexed_tracks = 0
        def inspect_index(end):
            nonlocal indexed_tracks
            for kind, box_end in boxes(file, end):
                assert kind != b'mvex', 'Fragmented index instead of ordinary MP4'
                if kind in (b'trak', b'mdia', b'minf', b'stbl'): inspect_index(box_end)
                elif kind == b'stsz':
                    _, _, samples = struct.unpack('>III', file.read(12))
                    assert samples > 0, 'Empty sample index'
                    indexed_tracks += 1
        for kind, end in boxes(file, path.stat().st_size):
            assert kind != b'moof', 'Fragmented MP4 instead of ordinary MP4'
            if kind == b'moov': inspect_index(end)
        assert indexed_tracks == 2, 'Expected full indexes for video and audio'

def inspect(path):
    with av.open(str(path)) as container:
        assert len(container.streams.video) == 1 and len(container.streams.audio) == 1, 'Expected one video and audio track'
        assert container.streams.video[0].codec_context.name == 'h264'
        assert container.streams.audio[0].codec_context.name == 'aac'
        video_count = 0; video_start = video_end = last_key = None
        for frame in container.decode(video=0):
            if video_count == 0:
                assert frame.key_frame, 'Video does not start with a keyframe'
                video_start = frame.time
            else:
                delta = float(frame.time - video_end)
                assert 0 < delta < .15, f'Video timestamp gap {delta}'
            if frame.key_frame:
                if last_key is not None: assert frame.time - last_key <= 1.1, 'Keyframe interval exceeds one second'
                last_key = frame.time
            video_end = frame.time; video_count += 1
    with av.open(str(path)) as container:
        audio_count = 0; audio_start = audio_end = expected = None
        for frame in container.decode(audio=0):
            if audio_count == 0: audio_start = frame.time
            else: assert abs(frame.time - expected) < .003, f'Audio timestamp gap {frame.time - expected}'
            audio_end = frame.time; expected = frame.time + frame.samples / frame.sample_rate; audio_count += 1
    assert video_count and audio_count, 'Empty track'
    start_skew = abs(float(video_start) - float(audio_start))
    assert start_skew < .1, f'A/V skew {start_skew}'
    result = {'file': path.name, 'video_frames': video_count, 'audio_frames': audio_count,
              'video_start': video_start, 'video_end': video_end,
              'audio_start': audio_start, 'audio_end': audio_end, 'start_skew_ms': round(start_skew * 1000, 2)}
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        for fraction in (.75, .25, .6, .1, 0):
            target = video_end * fraction
            container.seek(int(target / stream.time_base), stream=stream, backward=True, any_frame=False)
            frames = []; after_target = 0
            for frame in container.decode(video=0):
                frames.append(frame.time)
                if frame.time >= target: after_target += 1
                if after_target >= 10: break
            assert frames, f'No video after seek to {target}'
            assert max(0, target - 1.1) <= frames[0] <= target + .1, f'Wrong seek position: {target} -> {frames[0]}'
            assert all(0 < b-a < .15 for a,b in zip(frames, frames[1:])), 'Video stalled after seek'
            assert after_target == 10 or target > video_end - .4, 'Video stopped after seek'
    return result

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=pathlib.Path)
    parser.add_argument('--require-index', action='store_true', help='Require ordinary MP4 with populated sample indexes')
    args = parser.parse_args()
    files = sorted(p for p in args.folder.rglob('*.mp4') if not p.name.endswith('.partial.mp4'))
    assert files, 'No MP4 files'
    if args.require_index:
        for file in files: verify_regular_index(file)
    print(json.dumps([inspect(file) for file in files], indent=2))
