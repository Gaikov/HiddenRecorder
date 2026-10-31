#define HR_EXPORTS
#include "media.h"
#include <gst/gst.h>
#include <gst/app/gstappsink.h>
#include <gst/app/gstappsrc.h>
#include <gst/video/videooverlay.h>
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <set>

namespace {
constexpr uint64_t budget = 128ull * 1024 * 1024;
thread_local std::string last_error;
std::once_flag initialized;
std::filesystem::path utf8_path(const std::string& value) { return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(value.data()), value.size())); }
void initialize() {
    std::call_once(initialized, [] {
        // This is a desktop GUI: scan the bundled plugins in-process, without a console helper.
        gst_registry_fork_set_enabled(FALSE);
        gst_init(nullptr, nullptr);
    });
}
std::string quote(const std::string& input) {
    std::ostringstream out; out << '"';
    for (unsigned char ch : input) {
        if (ch == '"' || ch == '\\') out << '\\' << ch;
        else if (ch < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(ch);
        else out << ch;
    }
    out << '"'; return out.str();
}
GstElement* parse(const std::string& text) {
    GError* error = nullptr; auto* element = gst_parse_launch(text.c_str(), &error);
    if (error) { std::string message = error->message; g_error_free(error); if (element) gst_object_unref(element); throw std::runtime_error(message); }
    if (!element) throw std::runtime_error("Cannot create GStreamer pipeline");
    return element;
}
std::string bus_error(GstMessage* message) {
    GError* error = nullptr; gchar* debug = nullptr; gst_message_parse_error(message, &error, &debug);
    std::string result = error ? error->message : "Media error";
    if (debug) result += " | " + std::string(debug);
    g_clear_error(&error); g_free(debug); return result;
}
struct Packet {
    GstBuffer* buffer; GstCaps* caps; bool video; uint64_t pts; bool key;
    Packet(GstSample* sample, bool is_video) : buffer(gst_buffer_ref(gst_sample_get_buffer(sample))),
        caps(gst_caps_ref(gst_sample_get_caps(sample))), video(is_video), pts(GST_BUFFER_PTS(buffer)),
        key(is_video && !GST_BUFFER_FLAG_IS_SET(buffer, GST_BUFFER_FLAG_DELTA_UNIT)) {}
    ~Packet() { gst_buffer_unref(buffer); gst_caps_unref(caps); }
    uint64_t size() const { return gst_buffer_get_size(buffer); }
};
using PacketPtr = std::shared_ptr<Packet>;

class EncodedRing {
    std::deque<PacketPtr> packets;
    uint64_t bytes = 0, latest_video = 0;
public:
    void clear() { packets.clear(); bytes = latest_video = 0; }
    void push(PacketPtr packet, int pre_seconds) {
        packets.push_back(packet); bytes += packet->size();
        if (packet->video) latest_video = std::max(latest_video, packet->pts);
        // Keep an additional finalization window, so triggers received during EOS retain their history.
        uint64_t retention = uint64_t(pre_seconds + 12) * GST_SECOND;
        uint64_t cutoff = latest_video > retention ? latest_video - retention : 0, anchor = 0;
        for (const auto& item : packets) if (item->key && item->pts <= cutoff) anchor = std::max(anchor, item->pts);
        for (auto it = packets.begin(); it != packets.end();) {
            if ((*it)->pts < anchor) { bytes -= (*it)->size(); it = packets.erase(it); } else ++it;
        }
        if (bytes > budget) throw std::runtime_error("Pre-recording buffer exceeded 128 MiB; reduce bitrate or pre-roll");
    }
    bool ready() const {
        bool audio = false, video = false;
        for (const auto& p : packets) { audio |= !p->video; video |= p->key; if (audio && video) return true; }
        return false;
    }
    double seconds() const {
        for (const auto& p : packets) if (p->key) return double(latest_video - std::min(latest_video, p->pts)) / GST_SECOND;
        return 0;
    }
    std::vector<PacketPtr> snapshot(uint64_t trigger, int pre, uint64_t& origin) const {
        const uint64_t target = trigger > uint64_t(pre) * GST_SECOND ? trigger - uint64_t(pre) * GST_SECOND : 0;
        origin = GST_CLOCK_TIME_NONE;
        for (const auto& p : packets) if (p->key) {
            if (origin == GST_CLOCK_TIME_NONE) origin = p->pts;
            if (p->pts <= target) origin = p->pts;
        }
        if (origin == GST_CLOCK_TIME_NONE) throw std::runtime_error("Video keyframe not available");
        if (origin > trigger) throw std::runtime_error("Trigger history has expired");
        std::vector<PacketPtr> result;
        for (const auto& p : packets) if (p->pts >= origin) result.push_back(p);
        return result;
    }
};

class EventQueue {
    std::mutex mutex;
    std::deque<hr_event> events;
public:
    void emit(int type, const std::string& text = "", double first = 0, double second = 0, uint64_t timestamp = 0, uint64_t duration = 0) {
        hr_event event{}; event.type = type; event.first = first; event.second = second; event.time_ns = timestamp; event.duration_ns = duration;
        strncpy_s(event.text, text.c_str(), _TRUNCATE);
        std::lock_guard lock(mutex);
        if (events.size() > 10000) { events.clear(); event.type = 6; strcpy_s(event.text, "Application event queue overflow"); }
        events.push_back(event);
    }
    bool poll(hr_event& result) { std::lock_guard lock(mutex); if (events.empty()) return false; result = events.front(); events.pop_front(); return true; }
    void clear() { std::lock_guard lock(mutex); events.clear(); }
};

class Mp4Writer {
    EventQueue& events;
    GstElement *pipeline = nullptr, *video = nullptr, *audio = nullptr;
    GstBus* bus = nullptr;
    uint64_t origin;
    std::mutex mutex;
    std::condition_variable wake;
    std::deque<PacketPtr> queue;
    uint64_t queued_bytes = 0;
    bool ending = false;
    std::atomic<bool> cancelled{false};
    std::atomic<ULONGLONG> finish_deadline{0};
    std::thread worker;
    std::string error;
    std::vector<std::string> open_files;
    std::vector<std::string> closed_files;
    void rename_closed() {
        for (auto it = closed_files.begin(); it != closed_files.end();) {
            auto final_name = *it;
            auto pos = final_name.rfind(".partial.mp4");
            if (pos != std::string::npos) final_name.replace(pos, 12, ".mp4");
            flush_file(*it);
            std::error_code ec;
            std::filesystem::rename(utf8_path(*it), utf8_path(final_name), ec);
            if (ec) { ++it; continue; } // splitmux posts 'closed' before filesink releases its Windows handle.
            std::erase(open_files, *it); events.emit(5, final_name); it = closed_files.erase(it);
        }
    }
    static void flush_file(const std::string& name) {
        const auto path = utf8_path(name);
        HANDLE handle = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle != INVALID_HANDLE_VALUE) { FlushFileBuffers(handle); CloseHandle(handle); }
    }
    void drain_bus(bool& eos) {
        while (auto* message = gst_bus_pop(bus)) {
            const auto type = GST_MESSAGE_TYPE(message);
            if (type == GST_MESSAGE_ERROR) { auto text = bus_error(message); gst_message_unref(message); throw std::runtime_error(text); }
            if (type == GST_MESSAGE_EOS) eos = true;
            if (type == GST_MESSAGE_ELEMENT) {
                auto* s = gst_message_get_structure(message);
                const char* location = gst_structure_get_string(s, "location");
                if (location && gst_structure_has_name(s, "splitmuxsink-fragment-opened")) {
                    open_files.emplace_back(location); events.emit(4, location);
                }
                if (location && gst_structure_has_name(s, "splitmuxsink-fragment-closed")) {
                    closed_files.emplace_back(location);
                }
            }
            gst_message_unref(message);
        }
    }
    void run() noexcept {
        try {
            bool eos = false, sent_eos = false, configured_video = false, configured_audio = false;
            auto end_deadline = std::chrono::steady_clock::time_point::max();
            auto next_flush = std::chrono::steady_clock::now() + std::chrono::seconds(1);
            while (!cancelled && !eos) {
                PacketPtr packet;
                {
                    std::unique_lock lock(mutex);
                    wake.wait_for(lock, std::chrono::milliseconds(5), [&] { return cancelled || ending || !queue.empty(); });
                    if (!queue.empty()) { packet = queue.front(); queue.pop_front(); queued_bytes -= packet->size(); }
                    else if (ending && !sent_eos) {
                        gst_app_src_end_of_stream(GST_APP_SRC(video)); gst_app_src_end_of_stream(GST_APP_SRC(audio));
                        sent_eos = true; end_deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
                    }
                }
                if (packet) {
                    auto* source = packet->video ? video : audio;
                    bool& configured = packet->video ? configured_video : configured_audio;
                    if (!configured) { gst_app_src_set_caps(GST_APP_SRC(source), packet->caps); configured = true; }
                    guint64 video_bytes = 0, audio_bytes = 0;
                    g_object_get(video, "current-level-bytes", &video_bytes, nullptr); g_object_get(audio, "current-level-bytes", &audio_bytes, nullptr);
                    { std::lock_guard lock(mutex); if (queued_bytes + video_bytes + audio_bytes + packet->size() > budget) throw std::runtime_error("Recording queue exceeded 128 MiB; disk is too slow"); }
                    if (packet->pts >= origin) {
                        auto* buffer = gst_buffer_copy(packet->buffer);
                        GST_BUFFER_PTS(buffer) -= origin;
                        if (GST_BUFFER_DTS_IS_VALID(buffer)) GST_BUFFER_DTS(buffer) = GST_BUFFER_DTS(buffer) >= origin ? GST_BUFFER_DTS(buffer) - origin : 0;
                        if (gst_app_src_push_buffer(GST_APP_SRC(source), buffer) != GST_FLOW_OK) throw std::runtime_error("MP4 writer rejected a media packet");
                    }
                }
                drain_bus(eos);
                rename_closed();
                const auto now = std::chrono::steady_clock::now();
                if (finish_deadline && GetTickCount64() > finish_deadline) throw std::runtime_error("Recording drain/finalization exceeded 10 seconds");
                if (now > end_deadline) throw std::runtime_error("MP4 finalization timed out after 10 seconds");
                if (now >= next_flush) { for (const auto& path : open_files) flush_file(path); next_flush = now + std::chrono::seconds(1); }
            }
            if (cancelled) throw std::runtime_error("MP4 writer cancelled");
        } catch (const std::exception& ex) { error = ex.what(); events.emit(6, error); }
        gst_element_set_state(pipeline, GST_STATE_NULL);
        rename_closed();
        if (!closed_files.empty() && error.empty()) { error = "Cannot rename finalized MP4; file remains .partial.mp4"; events.emit(6, error); }
    }
public:
    Mp4Writer(EventQueue& sink, const std::string& directory, int part_seconds, uint64_t start) : events(sink), origin(start) {
        try {
            pipeline = parse("appsrc name=video format=time is-live=false block=false max-bytes=134217728 ! h264parse ! queue max-size-buffers=0 max-size-bytes=8388608 max-size-time=0 ! mux.video appsrc name=audio format=time is-live=false block=false max-bytes=134217728 ! aacparse ! queue max-size-buffers=0 max-size-bytes=1048576 max-size-time=0 ! mux.audio_0 splitmuxsink name=mux async-finalize=true muxer-factory=mp4mux");
            video = gst_bin_get_by_name(GST_BIN(pipeline), "video"); audio = gst_bin_get_by_name(GST_BIN(pipeline), "audio");
            auto* mux = gst_bin_get_by_name(GST_BIN(pipeline), "mux");
            SYSTEMTIME now; GetLocalTime(&now); static std::atomic<unsigned> sequence{0};
            char name[160]; sprintf_s(name, "HR_%04u%02u%02u_%02u%02u%02u_%03u_%lu_%u_part%%05d.partial.mp4", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentProcessId(), sequence++);
            const auto location_u8 = (utf8_path(directory) / name).u8string();
            std::string location(location_u8.begin(), location_u8.end());
            auto* properties = gst_structure_new("properties", "fragment-duration", G_TYPE_UINT, 1000u, nullptr);
            auto* sink_properties = gst_structure_new("properties", "buffer-mode", G_TYPE_INT, 4, nullptr);
            g_object_set(mux, "location", location.c_str(), "max-size-time", guint64(part_seconds) * GST_SECOND, "max-size-bytes", guint64(0), "muxer-properties", properties, "sink-properties", sink_properties, nullptr);
            // Request-pad linking in gst_parse_launch already creates the first muxer/sink.
            // Factory properties affect later parts; apply them to the initial children too.
            auto* children = gst_bin_iterate_recurse(GST_BIN(mux)); GValue child = G_VALUE_INIT;
            while (gst_iterator_next(children, &child) == GST_ITERATOR_OK) {
                auto* object = G_OBJECT(g_value_get_object(&child));
                if (g_object_class_find_property(G_OBJECT_GET_CLASS(object), "fragment-duration")) g_object_set(object, "fragment-duration", guint(1000), nullptr);
                if (g_object_class_find_property(G_OBJECT_GET_CLASS(object), "buffer-mode")) g_object_set(object, "buffer-mode", 4, nullptr);
                g_value_reset(&child);
            }
            g_value_unset(&child); gst_iterator_free(children);
            gst_structure_free(properties); gst_structure_free(sink_properties); gst_object_unref(mux);
            bus = gst_element_get_bus(pipeline);
            if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) throw std::runtime_error("Cannot start MP4 writer");
            worker = std::thread([this] { run(); });
        } catch (...) { cleanup(); throw; }
    }
    ~Mp4Writer() { cancelled = true; wake.notify_all(); if (worker.joinable()) worker.join(); cleanup(); }
    void cleanup() {
        if (pipeline) gst_element_set_state(pipeline, GST_STATE_NULL);
        if (bus) gst_object_unref(bus); if (video) gst_object_unref(video); if (audio) gst_object_unref(audio); if (pipeline) gst_object_unref(pipeline);
        bus = nullptr; video = audio = pipeline = nullptr;
    }
    void push(PacketPtr packet) {
        if (packet->pts < origin) return;
        std::lock_guard lock(mutex);
        if (ending) throw std::runtime_error("Packet delivered after writer EOS");
        if (queued_bytes + packet->size() > budget) throw std::runtime_error("Recording queue exceeded 128 MiB");
        queued_bytes += packet->size(); queue.push_back(std::move(packet)); wake.notify_one();
    }
    void finish() {
        finish_deadline = GetTickCount64() + 10000;
        { std::lock_guard lock(mutex); ending = true; } wake.notify_all();
        if (worker.joinable()) worker.join();
        if (!error.empty()) throw std::runtime_error(error);
    }
};

class CaptureEngine {
    GstElement *pipeline = nullptr, *preview = nullptr;
    GstBus* bus = nullptr;
    std::thread bus_thread;
    std::atomic<bool> stop_bus{false}, failed{false};
    std::atomic<ULONGLONG> last_video{0}, last_audio{0};
    std::mutex media_mutex;
    struct FrameTiming { uint64_t pts, duration; };
    std::deque<FrameTiming> input_timings;
    unsigned timing_logs = 0;
    EncodedRing ring;
    std::shared_ptr<Mp4Writer> writer;
    bool ready_sent = false;
    bool test_source = false;
    int pre = 5, part = 3600;
    std::string output;
    uintptr_t hwnd = 0;
    int width = 640, height = 360;
    static GstPadProbeReturn remember_frame(GstPad* pad, GstPadProbeInfo* info, gpointer data) {
        auto* self = static_cast<CaptureEngine*>(data);
        auto* buffer = GST_PAD_PROBE_INFO_BUFFER(info);
        if (!buffer) return GST_PAD_PROBE_OK;
        std::lock_guard lock(self->media_mutex);
        if (!GST_BUFFER_PTS_IS_VALID(buffer) || self->input_timings.size() >= 300) {
            if (!self->failed.exchange(true)) self->events.emit(6, "Camera frame timing unavailable or encoder stalled");
            return GST_PAD_PROBE_DROP;
        }
        auto pts = GST_BUFFER_PTS(buffer);
        if (auto* event = gst_pad_get_sticky_event(pad, GST_EVENT_SEGMENT, 0)) {
            const GstSegment* segment; gst_event_parse_segment(event, &segment);
            pts = gst_segment_to_running_time(segment, GST_FORMAT_TIME, pts); gst_event_unref(event);
        }
        self->input_timings.push_back({pts, GST_BUFFER_DURATION(buffer)});
        if (g_getenv("HIDDENRECORDER_TRACE_TIMESTAMPS") && self->timing_logs++ < 40) fprintf(stderr, "INPUT pts=%llu queue=%zu\n", pts, self->input_timings.size());
        return GST_PAD_PROBE_OK;
    }
    static GstFlowReturn sample_callback(GstAppSink* sink, gpointer data) {
        auto* self = static_cast<CaptureEngine*>(data);
        GstSample* sample = gst_app_sink_pull_sample(sink);
        if (!sample) return GST_FLOW_EOS;
        try {
            const bool is_video = std::string(GST_OBJECT_NAME(sink)) == "encoded_video";
            (is_video ? self->last_video : self->last_audio) = GetTickCount64();
            auto packet = std::make_shared<Packet>(sample, is_video);
            // Encoders may add a 1000-hour segment offset. Normalize before matching input frames.
            auto* segment = gst_sample_get_segment(sample);
            if (segment) {
                auto* converted = gst_buffer_copy(packet->buffer);
                if (GST_BUFFER_PTS_IS_VALID(converted)) GST_BUFFER_PTS(converted) = gst_segment_to_running_time(segment, GST_FORMAT_TIME, GST_BUFFER_PTS(converted));
                if (GST_BUFFER_DTS_IS_VALID(converted)) GST_BUFFER_DTS(converted) = gst_segment_to_running_time(segment, GST_FORMAT_TIME, GST_BUFFER_DTS(converted));
                gst_buffer_unref(packet->buffer); packet->buffer = converted; packet->pts = GST_BUFFER_PTS(converted);
            }
            // Some Media Foundation encoders omit timestamps on complete access units.
            // With B-frames disabled, match them to the original capture timestamps, never wall time.
            if (is_video) {
                if (packet->pts == GST_CLOCK_TIME_NONE &&
                    GST_BUFFER_FLAG_IS_SET(packet->buffer, GST_BUFFER_FLAG_HEADER) &&
                    !GST_BUFFER_FLAG_IS_SET(packet->buffer, GST_BUFFER_FLAG_MARKER)) {
                    gst_sample_unref(sample); return GST_FLOW_OK;
                }
                std::lock_guard lock(self->media_mutex);
                if (g_getenv("HIDDENRECORDER_TRACE_TIMESTAMPS") && self->timing_logs++ < 40) fprintf(stderr, "OUTPUT pts=%llu dts=%llu flags=%u bytes=%llu queue=%zu\n", packet->pts, GST_BUFFER_DTS(packet->buffer), GST_BUFFER_FLAGS(packet->buffer), packet->size(), self->input_timings.size());
                if (packet->pts == GST_CLOCK_TIME_NONE && !self->input_timings.empty()) {
                    auto timing = self->input_timings.front(); self->input_timings.pop_front();
                    auto* timed = gst_buffer_copy(packet->buffer);
                    GST_BUFFER_PTS(timed) = GST_BUFFER_DTS(timed) = timing.pts; GST_BUFFER_DURATION(timed) = timing.duration;
                    gst_buffer_unref(packet->buffer); packet->buffer = timed; packet->pts = timing.pts;
                } else if (packet->pts != GST_CLOCK_TIME_NONE) {
                    while (!self->input_timings.empty() && self->input_timings.front().pts <= packet->pts + GST_MSECOND) self->input_timings.pop_front();
                }
            }
            if (packet->pts == GST_CLOCK_TIME_NONE) {
                if (GST_BUFFER_FLAG_IS_SET(packet->buffer, GST_BUFFER_FLAG_HEADER) || GST_BUFFER_FLAG_IS_SET(packet->buffer, GST_BUFFER_FLAG_DECODE_ONLY)) {
                    gst_sample_unref(sample); return GST_FLOW_OK; // Codec configuration is retained in caps, not timed media.
                }
                throw std::runtime_error(std::string(is_video ? "Video" : "Audio") + " packet missing timestamp; flags=" + std::to_string(GST_BUFFER_FLAGS(packet->buffer)) + "; bytes=" + std::to_string(packet->size()));
            }
            std::lock_guard lock(self->media_mutex);
            self->ring.push(packet, self->pre);
            if (self->writer) self->writer->push(packet);
            if (!self->ready_sent && self->ring.ready()) { self->ready_sent = true; self->events.emit(1); }
        } catch (const std::exception& ex) { if (!self->failed.exchange(true)) self->events.emit(6, ex.what()); }
        gst_sample_unref(sample); return self->failed ? GST_FLOW_ERROR : GST_FLOW_OK;
    }
    static double maximum(const GValue* value) {
        if (!value) return -90;
        double result = -90;
        if (GST_VALUE_HOLDS_LIST(value)) {
            for (guint i = 0; i < gst_value_list_get_size(value); ++i) result = std::max(result, g_value_get_double(gst_value_list_get_value(value, i)));
        } else {
            auto* array = static_cast<GValueArray*>(g_value_get_boxed(value));
            if (array) for (guint i = 0; i < array->n_values; ++i) result = std::max(result, g_value_get_double(&array->values[i]));
        }
        return result;
    }
    void read_bus() {
        while (!stop_bus) {
            const auto now = GetTickCount64();
            if ((now - last_video > 10000 || now - last_audio > 10000) && !failed.exchange(true)) events.emit(6, "Camera or microphone stopped delivering data for 10 seconds");
            auto* message = gst_bus_timed_pop(bus, 20 * GST_MSECOND);
            if (!message) continue;
            if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR && !failed.exchange(true)) events.emit(6, bus_error(message));
            if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_WARNING) {
                GError* warning = nullptr; gchar* debug = nullptr;
                gst_message_parse_warning(message, &warning, &debug);
                if (warning && warning->domain == GST_CORE_ERROR && warning->code == GST_CORE_ERROR_CLOCK &&
                    g_strcmp0(GST_OBJECT_NAME(GST_MESSAGE_SRC(message)), "microphone") == 0 && !failed.exchange(true))
                    events.emit(6, std::string("Microphone lost audio samples: ") + warning->message + (debug ? std::string(" | ") + debug : ""));
                g_clear_error(&warning); g_free(debug);
            }
            if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ELEMENT) {
                auto* structure = gst_message_get_structure(message);
                if (gst_structure_has_name(structure, "level")) {
                    guint64 running = 0, duration = 0;
                    gst_structure_get_uint64(structure, "running-time", &running); gst_structure_get_uint64(structure, "duration", &duration);
                    events.emit(2, "", maximum(gst_structure_get_value(structure, "peak")), maximum(gst_structure_get_value(structure, "rms")), running + duration, duration);
                    std::lock_guard lock(media_mutex); events.emit(3, "", std::min(double(pre), ring.seconds()));
                }
            }
            gst_message_unref(message);
        }
    }
public:
    EventQueue events;
    ~CaptureEngine() { try { stop(); } catch (...) {} }
    void attach(uintptr_t handle, int w, int h) {
        hwnd = handle; width = w; height = h;
        if (preview && hwnd) { gst_video_overlay_set_window_handle(GST_VIDEO_OVERLAY(preview), hwnd); gst_video_overlay_set_render_rectangle(GST_VIDEO_OVERLAY(preview), 0, 0, width, height); }
    }
    void start(const hr_config& config) {
        stop(); events.clear(); failed = false; ready_sent = false; pre = config.pre_seconds; part = config.part_seconds; output = config.output;
        test_source = config.test_source != 0;
        bool hardware = !config.test_source;
        if (hardware) { auto* factory = gst_element_factory_find("mfh264enc"); hardware = factory != nullptr; if (factory) gst_object_unref(factory); }
        const std::string encoder = hardware ? "video/x-raw,format=NV12 ! mfh264enc name=encoder" : "video/x-raw,format=I420 ! openh264enc name=encoder";
        const std::string camera = config.test_source ? "videotestsrc is-live=true pattern=ball name=camera" : "mfvideosrc name=camera";
        const std::string mic = config.test_source ? "audiotestsrc is-live=true wave=sine freq=880 volume=0.5 name=microphone" : "wasapi2src name=microphone";
        const std::string sink = hwnd && !config.test_source ? "d3d11videosink name=preview sync=false" : "fakesink name=preview sync=false";
        auto* capture_caps = gst_caps_from_string(config.caps);
        if (!capture_caps || gst_caps_is_empty(capture_caps)) { if (capture_caps) gst_caps_unref(capture_caps); throw std::runtime_error("Invalid camera caps"); }
        int rate_n = 30, rate_d = 1; gst_structure_get_fraction(gst_caps_get_structure(capture_caps, 0), "framerate", &rate_n, &rate_d); gst_caps_unref(capture_caps);
        const std::string rate = " ! videorate ! video/x-raw,framerate=" + std::to_string(rate_n) + "/" + std::to_string(rate_d);
        try {
            // AAC negotiation/startup can block for hundreds of milliseconds. Keep draining
            // WASAPI on its own thread so its small hardware ring never loses those samples.
            pipeline = parse(camera + " ! capsfilter name=mode ! decodebin" + rate + " ! videoconvert ! tee name=v v. ! queue leaky=downstream max-size-buffers=2 ! " + sink + " v. ! queue ! videoconvert ! " + encoder + " ! h264parse config-interval=-1 ! video/x-h264,stream-format=avc,alignment=au ! appsink name=encoded_video sync=false " + mic + " ! queue max-size-buffers=0 max-size-bytes=4194304 max-size-time=2000000000 ! audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=" + std::to_string(config.channels) + " ! level interval=20000000 post-messages=true ! mfaacenc name=aac ! aacparse ! appsink name=encoded_audio sync=false");
            auto* camera_element = gst_bin_get_by_name(GST_BIN(pipeline), "camera");
            auto* mic_element = gst_bin_get_by_name(GST_BIN(pipeline), "microphone");
            if (config.test_source == 2) g_object_set(mic_element, "volume", 0.0, nullptr);
            if (!config.test_source) { g_object_set(camera_element, "device-path", config.camera, nullptr); g_object_set(mic_element, "device", config.microphone, nullptr); }
            gst_object_unref(camera_element); gst_object_unref(mic_element);
            auto* caps = gst_caps_from_string(config.caps); if (!caps) throw std::runtime_error("Invalid video caps");
            auto* filter = gst_bin_get_by_name(GST_BIN(pipeline), "mode"); g_object_set(filter, "caps", caps, nullptr); gst_object_unref(filter); gst_caps_unref(caps);
            auto* enc = gst_bin_get_by_name(GST_BIN(pipeline), "encoder");
            const int fps = [&] { auto* c = gst_caps_from_string(config.caps); int n = 30, d = 1; gst_structure_get_fraction(gst_caps_get_structure(c, 0), "framerate", &n, &d); gst_caps_unref(c); return std::max(1, n / std::max(1,d)); }();
            if (hardware) { g_object_set(enc, "bitrate", guint(config.video_kbps), "gop-size", guint(fps), "bframes", guint(0), nullptr); }
            else g_object_set(enc, "bitrate", guint(config.video_kbps * 1000), "gop-size", guint(fps), "enable-frame-skip", FALSE, nullptr);
            auto* encoder_input = gst_element_get_static_pad(enc, "sink");
            gst_pad_add_probe(encoder_input, GST_PAD_PROBE_TYPE_BUFFER, remember_frame, this, nullptr); gst_object_unref(encoder_input);
            gst_object_unref(enc);
            auto* aac = gst_bin_get_by_name(GST_BIN(pipeline), "aac"); g_object_set(aac, "bitrate", guint(config.audio_kbps * 1000), nullptr); gst_object_unref(aac);
            preview = gst_bin_get_by_name(GST_BIN(pipeline), "preview"); if (hwnd && !config.test_source) attach(hwnd, width, height);
            for (const char* name : {"encoded_video", "encoded_audio"}) {
                auto* app = gst_bin_get_by_name(GST_BIN(pipeline), name); GstAppSinkCallbacks callbacks{}; callbacks.new_sample = sample_callback;
                gst_app_sink_set_callbacks(GST_APP_SINK(app), &callbacks, this, nullptr); g_object_set(app, "max-buffers", guint(16), "drop", FALSE, nullptr); gst_object_unref(app);
            }
            bus = gst_element_get_bus(pipeline); stop_bus = false; last_video = last_audio = GetTickCount64(); bus_thread = std::thread([this] { read_bus(); });
            if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) throw std::runtime_error("Cannot start camera/microphone");
        } catch (...) { stop(); throw; }
    }
    void begin(uint64_t trigger) {
        std::lock_guard lock(media_mutex);
        if (writer) throw std::runtime_error("Recording already active");
        uint64_t origin; auto snapshot = ring.snapshot(trigger, pre, origin);
        auto next = std::make_shared<Mp4Writer>(events, output, part, origin);
        for (auto& packet : snapshot) next->push(packet);
        writer = std::move(next);
    }
    void test_volume(double volume) {
        if (!test_source || !pipeline || !std::isfinite(volume) || volume < 0 || volume > 1)
            throw std::runtime_error("Test volume requires a running synthetic source and a value between 0 and 1");
        auto* microphone = gst_bin_get_by_name(GST_BIN(pipeline), "microphone");
        g_object_set(microphone, "volume", volume, nullptr); gst_object_unref(microphone);
    }
    void finish() {
        std::shared_ptr<Mp4Writer> previous;
        { std::lock_guard lock(media_mutex); previous = std::move(writer); }
        if (previous) previous->finish();
    }
    void stop() {
        std::exception_ptr error;
        try { finish(); } catch (...) { error = std::current_exception(); }
        if (pipeline) gst_element_set_state(pipeline, GST_STATE_NULL);
        stop_bus = true; if (bus_thread.joinable()) bus_thread.join();
        if (bus) gst_object_unref(bus); if (preview) gst_object_unref(preview); if (pipeline) gst_object_unref(pipeline);
        bus = nullptr; preview = pipeline = nullptr;
        { std::lock_guard lock(media_mutex); ring.clear(); input_timings.clear(); }
        if (error) std::rethrow_exception(error);
    }
};

std::string device_json(GstDevice* device, bool video) {
    auto* source = gst_device_create_element(device, nullptr); if (!source) return "";
    auto* factory = gst_element_get_factory(source);
    const std::string factory_name = factory ? gst_plugin_feature_get_name(GST_PLUGIN_FEATURE(factory)) : "";
    if (factory_name != (video ? "mfvideosrc" : "wasapi2src")) { gst_object_unref(source); return ""; }
    if (!video) { gboolean loopback = FALSE; g_object_get(source, "loopback", &loopback, nullptr); if (loopback) { gst_object_unref(source); return ""; } }
    gchar* id = nullptr; g_object_get(source, video ? "device-path" : "device", &id, nullptr);
    gchar* name = gst_device_get_display_name(device);
    std::ostringstream result; result << "{\"id\":" << quote(id ? id : "") << ",\"name\":" << quote(name ? name : "") << ",\"modes\":[";
    g_free(id); g_free(name); gst_object_unref(source);
    if (video) {
        auto* supported = gst_device_get_caps(device); bool first = true;
        std::set<std::pair<int,int>> sizes{{320,240},{640,360},{640,480},{800,600},{1024,768},{1280,720},{1280,960},{1920,1080},{1920,1200},{2560,1440},{3840,2160}};
        std::set<std::pair<int,int>> rates{{10,1},{15,1},{24,1},{25,1},{30000,1001},{30,1},{50,1},{60000,1001},{60,1}};
        for (guint i = 0; i < gst_caps_get_size(supported); ++i) {
            const auto* s = gst_caps_get_structure(supported, i); int w, h, n, d;
            if (gst_structure_get_int(s, "width", &w) && gst_structure_get_int(s, "height", &h)) sizes.emplace(w,h);
            if (gst_structure_get_fraction(s, "framerate", &n, &d)) rates.emplace(n,d);
            const auto* fps = gst_structure_get_value(s, "framerate");
            if (fps && GST_VALUE_HOLDS_LIST(fps)) for (guint j = 0; j < gst_value_list_get_size(fps); ++j) {
                auto* v = gst_value_list_get_value(fps, j); if (GST_VALUE_HOLDS_FRACTION(v)) rates.emplace(gst_value_get_fraction_numerator(v), gst_value_get_fraction_denominator(v));
            }
        }
        for (auto [w,h] : sizes)
            for (auto [n,d] : rates)
                for (const char* type : {"video/x-raw", "image/jpeg"}) {
                    auto* requested = gst_caps_new_simple(type, "width", G_TYPE_INT, w, "height", G_TYPE_INT, h, "framerate", GST_TYPE_FRACTION, n, d, nullptr);
                    auto* intersection = gst_caps_intersect(supported, requested); gst_caps_unref(requested);
                    if (!gst_caps_is_empty(intersection)) {
                        intersection = gst_caps_fixate(intersection); gchar* text = gst_caps_to_string(intersection);
                        if (!first) result << ','; first = false;
                        result << "{\"width\":" << w << ",\"height\":" << h << ",\"fpsNumerator\":" << n << ",\"fpsDenominator\":" << d << ",\"caps\":" << quote(text) << '}';
                        g_free(text); gst_caps_unref(intersection); break;
                    }
                    gst_caps_unref(intersection);
                }
        gst_caps_unref(supported);
    }
    result << "]}"; return result.str();
}
template<class F> int protect(F action) { try { action(); last_error.clear(); return 1; } catch (const std::exception& ex) { last_error = ex.what(); return 0; } }
}

HR_API void* hr_create() { try { initialize(); return new CaptureEngine; } catch (const std::exception& ex) { last_error = ex.what(); return nullptr; } }
HR_API void hr_destroy(void* h) { delete static_cast<CaptureEngine*>(h); }
HR_API int hr_start(void* h, const hr_config* c) { return protect([&] { static_cast<CaptureEngine*>(h)->start(*c); }); }
HR_API int hr_stop(void* h) { return protect([&] { static_cast<CaptureEngine*>(h)->stop(); }); }
HR_API int hr_begin(void* h, int64_t t) { return protect([&] { static_cast<CaptureEngine*>(h)->begin(t); }); }
HR_API int hr_finish(void* h) { return protect([&] { static_cast<CaptureEngine*>(h)->finish(); }); }
HR_API int hr_test_volume(void* h, double volume) { return protect([&] { static_cast<CaptureEngine*>(h)->test_volume(volume); }); }
HR_API int hr_poll(void* h, hr_event* event) { return static_cast<CaptureEngine*>(h)->events.poll(*event); }
HR_API void hr_preview(void* h, uintptr_t hwnd, int w, int height) { static_cast<CaptureEngine*>(h)->attach(hwnd, w, height); }
HR_API const char* hr_last_error() { return last_error.c_str(); }
HR_API int hr_catalog(char* json, int capacity) {
    return protect([&] {
        initialize(); auto* monitor = gst_device_monitor_new();
        gst_device_monitor_add_filter(monitor, "Video/Source", nullptr); gst_device_monitor_add_filter(monitor, "Audio/Source", nullptr);
        gst_device_monitor_start(monitor); auto* devices = gst_device_monitor_get_devices(monitor);
        std::string cameras, microphones;
        for (auto* node = devices; node; node = node->next) {
            auto* device = GST_DEVICE(node->data); gchar* cls = gst_device_get_device_class(device);
            const bool video = std::string(cls).find("Video") != std::string::npos; g_free(cls);
            auto text = device_json(device, video); auto& list = video ? cameras : microphones;
            if (!text.empty()) { if (!list.empty()) list += ','; list += text; }
        }
        g_list_free_full(devices, gst_object_unref); gst_device_monitor_stop(monitor); gst_object_unref(monitor);
        std::string result = "{\"cameras\":[" + cameras + "],\"microphones\":[" + microphones + "]}";
        if (result.size() + 1 > size_t(capacity)) throw std::runtime_error("Device catalog buffer too small");
        strcpy_s(json, capacity, result.c_str());
    });
}
