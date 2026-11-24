// mv_player: thin C API over the Metavision SDK Stream API for the WinForms RAW player.
// Playback is paced inside the CD callback (real_time_playback is disabled in the SDK) so that
// pause / speed / seek / loop are fully controlled here.
#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#endif
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <metavision/sdk/stream/camera.h>
#include <metavision/sdk/stream/file_config_hints.h>
#include <metavision/sdk/base/events/event_cd.h>

#ifdef _WIN32
#include <windows.h>
#include <mmsystem.h>
#define MVP_API extern "C" __declspec(dllexport)
#else
#define MVP_API extern "C" __attribute__((visibility("default")))
#endif

namespace {

using clock_t_ = std::chrono::steady_clock;
constexpr int64_t kNever = INT64_MIN / 4;

thread_local std::string g_last_error;

struct Player {
    std::unique_ptr<Metavision::Camera> cam;
    int width = 0, height = 0;
    int64_t seek_start = 0, seek_end = 0;

    // playback clock (event-time domain, microseconds)
    std::mutex clk_m;
    double pos_us = 0;
    clock_t_::time_point last_tick = clock_t_::now();
    bool playing = true;
    double speed = 1.0;

    std::atomic<bool> loop{false};
    std::atomic<bool> ended{false};
    std::atomic<bool> abort_wait{false};
    std::atomic<bool> shutdown{false};
    std::atomic<int64_t> window_us{20000};

    // per-pixel state for frame rendering
    std::mutex fr_m;
    std::vector<int64_t> last_ts;
    std::vector<uint8_t> pol;

    std::mutex ctl_m; // serializes start/stop/seek
    std::thread ctrl;

    void advance_clock() {
        std::lock_guard<std::mutex> l(clk_m);
        auto now = clock_t_::now();
        if (playing) {
            double dt = std::chrono::duration<double, std::micro>(now - last_tick).count();
            pos_us += dt * speed;
        }
        last_tick = now;
    }
    double position() {
        advance_clock();
        std::lock_guard<std::mutex> l(clk_m);
        return pos_us;
    }
    void set_position(double p) {
        std::lock_guard<std::mutex> l(clk_m);
        pos_us    = p;
        last_tick = clock_t_::now();
    }

    void clear_frame() {
        std::lock_guard<std::mutex> l(fr_m);
        std::fill(last_ts.begin(), last_ts.end(), kNever);
    }

    void on_events(const Metavision::EventCD *b, const Metavision::EventCD *e) {
        const Metavision::EventCD *p = b;
        while (p != e) {
            if (abort_wait.load() || shutdown.load())
                return;
            advance_clock();
            double pos;
            {
                std::lock_guard<std::mutex> l(clk_m);
                pos = pos_us;
            }
            {
                std::lock_guard<std::mutex> l(fr_m);
                while (p != e && static_cast<double>(p->t) <= pos) {
                    if (p->x < width && p->y < height) {
                        const size_t i = static_cast<size_t>(p->y) * width + p->x;
                        last_ts[i]     = p->t;
                        pol[i]         = static_cast<uint8_t>(p->p);
                    }
                    ++p;
                }
            }
            if (p != e)
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    // must be called with ctl_m held
    void restart_at(int64_t ts_us, int64_t preroll_us) {
        abort_wait = true;
        try {
            // always stop: after EOF is_running() is false but the camera is still in "started" state
            cam->stop();
        } catch (...) {
        }
        const int64_t target = std::max<int64_t>(std::max<int64_t>(seek_start, 0), ts_us - preroll_us);
        try {
            cam->offline_streaming_control().seek(target);
        } catch (...) {
        }
        clear_frame();
        set_position(static_cast<double>(ts_us));
        ended      = false;
        abort_wait = false;
        if (!cam->start())
            g_last_error = "camera restart failed";
    }

    void controller() {
        while (!shutdown.load()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            std::unique_lock<std::mutex> l(ctl_m, std::try_to_lock);
            if (!l.owns_lock() || shutdown.load() || ended.load())
                continue;
            bool running = true;
            try {
                running = cam->is_running();
            } catch (...) {
            }
            if (running)
                continue;
            // end of file reached and all events delivered
            if (loop.load()) {
                restart_at(seek_start, 0);
            } else {
                try {
                    cam->stop();
                } catch (...) {
                }
                set_position(static_cast<double>(seek_end));
                {
                    std::lock_guard<std::mutex> lc(clk_m);
                    playing = false;
                }
                ended = true;
            }
        }
    }
};

} // namespace

MVP_API const char *mvp_last_error() {
    return g_last_error.c_str();
}

MVP_API void *mvp_open(const char *utf8_path, int *width, int *height, int64_t *start_us, int64_t *end_us) {
    try {
#ifdef _WIN32
        timeBeginPeriod(1);
#endif
        auto p = std::make_unique<Player>();
        Metavision::FileConfigHints hints;
        hints.real_time_playback(false);
        std::filesystem::path path = std::filesystem::u8path(utf8_path);
        p->cam = std::make_unique<Metavision::Camera>(Metavision::Camera::from_file(path, hints));
        p->width  = p->cam->geometry().get_width();
        p->height = p->cam->geometry().get_height();
        try {
            auto &osc = p->cam->offline_streaming_control();
            p->seek_start = osc.get_seek_start_time();
            p->seek_end   = osc.get_seek_end_time();
            if (p->seek_end <= p->seek_start)
                p->seek_end = p->seek_start + osc.get_duration();
        } catch (...) {
            p->seek_start = 0;
            p->seek_end   = 0;
        }
        const size_t n = static_cast<size_t>(p->width) * p->height;
        p->last_ts.assign(n, kNever);
        p->pol.assign(n, 0);
        p->set_position(static_cast<double>(p->seek_start));

        Player *raw = p.get();
        p->cam->cd().add_callback([raw](const Metavision::EventCD *b, const Metavision::EventCD *e) { raw->on_events(b, e); });
        if (!p->cam->start()) {
            g_last_error = "camera.start() failed";
            return nullptr;
        }
        p->ctrl = std::thread([raw]() { raw->controller(); });

        *width    = p->width;
        *height   = p->height;
        *start_us = p->seek_start;
        *end_us   = p->seek_end;
        return p.release();
    } catch (const std::exception &ex) {
        g_last_error = ex.what();
        return nullptr;
    } catch (...) {
        g_last_error = "unknown error";
        return nullptr;
    }
}

MVP_API void mvp_close(void *h) {
    if (!h)
        return;
    Player *p = static_cast<Player *>(h);
    p->shutdown = true;
    p->abort_wait = true;
    if (p->ctrl.joinable())
        p->ctrl.join();
    try {
        if (p->cam && p->cam->is_running())
            p->cam->stop();
    } catch (...) {
    }
    delete p;
#ifdef _WIN32
    timeEndPeriod(1);
#endif
}

MVP_API void mvp_set_playing(void *h, int playing) {
    Player *p = static_cast<Player *>(h);
    if (playing && p->ended.load()) {
        std::lock_guard<std::mutex> l(p->ctl_m);
        {
            std::lock_guard<std::mutex> lc(p->clk_m);
            p->playing = true;
        }
        p->restart_at(p->seek_start, 0);
        return;
    }
    p->advance_clock(); // integrate time up to now with the old state
    std::lock_guard<std::mutex> l(p->clk_m);
    p->playing = playing != 0;
}

MVP_API int mvp_is_playing(void *h) {
    Player *p = static_cast<Player *>(h);
    std::lock_guard<std::mutex> l(p->clk_m);
    return p->playing ? 1 : 0;
}

MVP_API void mvp_set_speed(void *h, double speed) {
    Player *p = static_cast<Player *>(h);
    p->advance_clock();
    std::lock_guard<std::mutex> l(p->clk_m);
    p->speed = std::max(1e-4, speed);
}

MVP_API void mvp_set_loop(void *h, int loop) {
    static_cast<Player *>(h)->loop = loop != 0;
}

MVP_API void mvp_set_accumulation_us(void *h, int64_t us) {
    static_cast<Player *>(h)->window_us = std::max<int64_t>(100, us);
}

MVP_API int mvp_is_ended(void *h) {
    return static_cast<Player *>(h)->ended.load() ? 1 : 0;
}

MVP_API int mvp_seek(void *h, int64_t ts_us) {
    Player *p = static_cast<Player *>(h);
    ts_us     = std::min(std::max(ts_us, p->seek_start), p->seek_end);
    std::lock_guard<std::mutex> l(p->ctl_m);
    p->restart_at(ts_us, p->window_us.load());
    return 1;
}

MVP_API int64_t mvp_get_position_us(void *h) {
    Player *p = static_cast<Player *>(h);
    double pos = p->position();
    return static_cast<int64_t>(std::min<double>(pos, static_cast<double>(p->seek_end)));
}

// Renders the current frame into a caller-provided BGRA buffer (width*height*4, stride in bytes).
MVP_API void mvp_render_bgra(void *h, uint8_t *dst, int stride) {
    Player *p       = static_cast<Player *>(h);
    const double cur = p->position();
    const int64_t win = p->window_us.load();
    std::lock_guard<std::mutex> l(p->fr_m);
    const uint8_t bg[4]  = {30, 26, 24, 255};
    const uint8_t on[4]  = {255, 255, 255, 255};
    const uint8_t off[4] = {201, 126, 64, 255};
    for (int y = 0; y < p->height; ++y) {
        uint8_t *row = dst + static_cast<size_t>(y) * stride;
        const size_t base = static_cast<size_t>(y) * p->width;
        for (int x = 0; x < p->width; ++x) {
            const int64_t t = p->last_ts[base + x];
            const double age = cur - static_cast<double>(t);
            const uint8_t *c = bg;
            if (t != kNever && age >= 0 && age <= static_cast<double>(win))
                c = p->pol[base + x] ? on : off;
            std::memcpy(row + static_cast<size_t>(x) * 4, c, 4);
        }
    }
}
