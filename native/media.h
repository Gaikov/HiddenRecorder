#pragma once
#include <stdint.h>
#ifdef HR_EXPORTS
#define HR_API extern "C" __declspec(dllexport)
#else
#define HR_API extern "C" __declspec(dllimport)
#endif
struct hr_config {
    const char* camera; const char* microphone; const char* caps; const char* output;
    int pre_seconds, video_kbps, audio_kbps, channels, part_seconds, test_source;
};
// Events: 1 ready, 2 level, 3 buffered, 4 file opened, 5 file closed, 6 error.
struct hr_event { int type; double first, second; int64_t time_ns, duration_ns; char text[2048]; };
HR_API void* hr_create();
HR_API void hr_destroy(void* handle);
HR_API int hr_catalog(char* json, int capacity);
HR_API int hr_start(void* handle, const hr_config* config);
HR_API int hr_stop(void* handle);
HR_API int hr_begin(void* handle, int64_t trigger_ns);
HR_API int hr_finish(void* handle);
// Integration diagnostics only; rejects real microphone sessions.
HR_API int hr_test_volume(void* handle, double volume);
HR_API int hr_poll(void* handle, hr_event* result);
HR_API void hr_preview(void* handle, uintptr_t hwnd, int width, int height);
HR_API const char* hr_last_error();
