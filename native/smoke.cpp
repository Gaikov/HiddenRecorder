#include "media.h"
#include <chrono>
#include <filesystem>
#include <iostream>
#include <thread>
int main(int argc, char** argv) {
    if (argc > 1 && std::string(argv[1]) == "--devices") { char json[262144]; if (!hr_catalog(json, sizeof json)) { std::cerr << hr_last_error(); return 1; } std::cout << json; return 0; }
    std::string output = argc > 1 ? argv[1] : "smoke-output"; std::filesystem::create_directories(output);
    hr_config config{"", "", "video/x-raw,width=640,height=360,framerate=30/1", output.c_str(), 5, 1000, 128, 1, 5, 1};
    auto* engine = hr_create(); if (!engine) { std::cerr << hr_last_error(); return 1; }
    if (!hr_start(engine, &config)) { std::cerr << hr_last_error(); hr_destroy(engine); return 1; }
    bool recording = false; int files = 0; int64_t media = 0;
    auto start = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start < std::chrono::seconds(18)) {
        hr_event event{};
        while (hr_poll(engine, &event)) {
            if (event.type == 6) { std::cerr << event.text; hr_destroy(engine); return 1; }
            if (event.type == 2) media = event.time_ns;
            if (event.type == 5) { ++files; std::cout << "Closed: " << event.text << '\n'; }
        }
        if (!recording && media > 6000000000ll) { if (!hr_begin(engine, media)) { std::cerr << hr_last_error(); hr_destroy(engine); return 1; } recording = true; }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    int result = hr_finish(engine); if (!result) std::cerr << hr_last_error();
    hr_event event{}; while (hr_poll(engine, &event)) { if (event.type == 5) { ++files; std::cout << "Closed: " << event.text << '\n'; } if (event.type == 6) { result = 0; std::cerr << event.text; } }
    hr_stop(engine); hr_destroy(engine); std::cout << "Parts: " << files << '\n'; return result && files >= 2 ? 0 : 1;
}
