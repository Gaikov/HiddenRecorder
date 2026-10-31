// Checks the Windows Media Foundation demuxer/decoder, independently of FFmpeg.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <propvarutil.h>
#include <wrl/client.h>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <algorithm>

using Microsoft::WRL::ComPtr;
constexpr DWORD video_stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
constexpr DWORD all_streams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD media_source = static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE);
static void check(HRESULT result, const char* operation) {
    if (FAILED(result)) { std::ostringstream error; error << operation << ": HRESULT 0x" << std::hex << result; throw std::runtime_error(error.str()); }
}
static void inspect(const wchar_t* path) {
    ComPtr<IMFSourceReader> reader;
    check(MFCreateSourceReaderFromURL(path, nullptr, &reader), "Open MP4");
    check(reader->SetStreamSelection(all_streams, FALSE), "Deselect streams");
    check(reader->SetStreamSelection(video_stream, TRUE), "Select video");
    ComPtr<IMFMediaType> type; check(MFCreateMediaType(&type), "Create decoded format");
    check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Video type");
    check(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12), "NV12 type");
    check(reader->SetCurrentMediaType(video_stream, nullptr, type.Get()), "Enable H264 decoding");
    PROPVARIANT duration; PropVariantInit(&duration);
    check(reader->GetPresentationAttribute(media_source, MF_PD_DURATION, &duration), "Get duration");
    const double seconds = double(duration.uhVal.QuadPart) / 10000000.; PropVariantClear(&duration);
    if (seconds <= 0) throw std::runtime_error("Empty MP4 duration");
    std::cout << "Windows duration: " << seconds << " s\n";
    for (double fraction : { .75, .25, .60, .10, .0 }) {
        const double target = seconds * fraction;
        PROPVARIANT position; InitPropVariantFromInt64(static_cast<LONGLONG>(target * 10000000.), &position);
        check(reader->SetCurrentPosition(GUID_NULL, position), "Seek"); PropVariantClear(&position);
        double first = -1, last = -1; unsigned frames = 0, frames_after_target = 0;
        bool ended = false;
        for (unsigned attempt = 0; attempt < 300 && frames_after_target < 15; ++attempt) {
            DWORD flags = 0; LONGLONG timestamp = 0; ComPtr<IMFSample> sample;
            check(reader->ReadSample(video_stream, 0, nullptr, &flags, &timestamp, &sample), "Decode after seek");
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { ended = true; break; }
            if (!sample) continue;
            const auto current = double(timestamp) / 10000000.;
            if (!frames) first = current;
            else if (current <= last || current - last > .15) throw std::runtime_error("Decoded video timestamps stall/jump after seeking");
            last = current; ++frames;
            if (current >= target) ++frames_after_target;
        }
        std::cout << "seek " << target << " -> " << first << " .. " << last << ", frames=" << frames << std::endl;
        if (!frames_after_target || (frames_after_target < 15 && (!ended || last < seconds - .15)) ||
            first < std::max(0., target - 1.1) || first > target + .1)
            throw std::runtime_error("Windows decoded video is not at the requested seek position");
    }
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) { std::cerr << "Usage: mf-seek-check FILE.mp4\n"; return 2; }
    HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com)) return 1;
    HRESULT mf = MFStartup(MF_VERSION);
    if (FAILED(mf)) { CoUninitialize(); return 1; }
    int result = 0;
    try { inspect(argv[1]); std::cout << "PASS Windows forward/backward seeking\n"; }
    catch (const std::exception& ex) { std::cerr << ex.what() << std::endl; result = 1; }
    MFShutdown(); CoUninitialize(); return result;
}
