#include "media.cpp"
#include <iostream>

static PacketPtr packet(bool video, int seconds, bool key = false) {
    auto* buffer = gst_buffer_new_allocate(nullptr, 16, nullptr); GST_BUFFER_PTS(buffer) = uint64_t(seconds) * GST_SECOND;
    if (video && !key) GST_BUFFER_FLAG_SET(buffer, GST_BUFFER_FLAG_DELTA_UNIT);
    auto* caps = gst_caps_from_string(video ? "video/x-h264" : "audio/mpeg");
    auto* sample = gst_sample_new(buffer, caps, nullptr, nullptr);
    auto result = std::make_shared<Packet>(sample, video); gst_sample_unref(sample); gst_caps_unref(caps); gst_buffer_unref(buffer); return result;
}
static void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
int main() {
    initialize();
    try {
        require(!gst_registry_fork_is_enabled(), "Desktop startup must not spawn a console plugin scanner");
        EncodedRing ring; require(!ring.ready(), "Empty ring is not ready");
        for (int i = 0; i <= 40; ++i) { ring.push(packet(true, i, i % 2 == 0), 5); ring.push(packet(false, i), 5); }
        uint64_t origin; auto result = ring.snapshot(40 * GST_SECOND, 5, origin);
        require(origin == 34 * GST_SECOND, "Pre-roll must start at previous keyframe"); require(result.size() == 14, "Snapshot must include both streams without duplication");
        require(result.front()->key, "First video must be keyframe");
        auto pending = ring.snapshot(31 * GST_SECOND, 5, origin);
        require(origin == 26 * GST_SECOND, "Finalization window must preserve old trigger");
        require(pending.size() == 30, "Pending trigger must retain complete timeline");
        EncodedRing early; early.push(packet(true, 0, true), 5); early.push(packet(false, 0), 5);
        require(early.ready(), "Early trigger readiness"); auto initial = early.snapshot(0, 5, origin); require(initial.size() == 2 && origin == 0, "Early trigger uses available history");
        ring.clear(); require(!ring.ready(), "Stop releases ring");
        std::cout << "Ring tests passed\n"; return 0;
    } catch (const std::exception& ex) { std::cerr << ex.what(); return 1; }
}
