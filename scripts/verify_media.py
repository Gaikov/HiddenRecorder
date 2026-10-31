"""Decode all frames and verify continuous timestamps. Requires PyAV (pip install av)."""
import av, json, pathlib, sys

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
        container.seek(int(max(0, video_end / 2) * av.time_base))
        assert next(container.decode(video=0), None) is not None, 'Seeking failed'
    return result

if __name__ == '__main__':
    files = sorted(p for p in pathlib.Path(sys.argv[1]).glob('*.mp4') if not p.name.endswith('.partial.mp4'))
    assert files, 'No MP4 files'
    print(json.dumps([inspect(file) for file in files], indent=2))
