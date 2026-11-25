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
#include <cmath>
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


// ---------------------------------------------------------------------------------------------
// Vibration analysis inside user-defined ROIs.
// For every pixel inside a ROI the time between the starts of two consecutive event bursts of the
// same polarity is one period sample of whatever passes that pixel (an oscillating edge crosses a
// pixel once per cycle and polarity).  1 / period is accumulated into a 1 Hz histogram per ROI over
// a sliding window; peaks of that histogram are the vibration frequencies in the ROI.
// ---------------------------------------------------------------------------------------------
struct Analysis {
    static constexpr int kMaxRoi = 8;
    struct Roi {
        bool active = false;
        int x0 = 0, y0 = 0, x1 = 0, y1 = 0; // x1/y1 exclusive
        std::vector<float> ring;             // n_slices * (max_hz + 1)
        std::vector<int64_t> slice_id;       // absolute slice index stored in every ring slot
    };

    std::mutex m;
    std::atomic<int> nactive{0};
    int W = 0, H = 0;
    int max_hz = 500;
    int64_t slice_us = 100000;
    int n_slices = 10;
    int64_t burst_us = 500;
    int64_t latest = kNever;
    std::vector<int64_t> last_start[2];
    std::vector<int64_t> last_ev[2];
    Roi roi[kMaxRoi];

    int bins() const { return max_hz + 1; }

    void alloc_state_locked() {
        const size_t n = static_cast<size_t>(W) * H;
        for (int q = 0; q < 2; ++q) {
            last_start[q].assign(n, kNever);
            last_ev[q].assign(n, kNever);
        }
        latest = kNever;
    }
    void clear_roi_hist_locked(Roi &r) {
        r.ring.assign(static_cast<size_t>(n_slices) * bins(), 0.f);
        r.slice_id.assign(n_slices, kNever);
    }
    void reset_state() {
        std::lock_guard<std::mutex> l(m);
        if (nactive.load() > 0)
            alloc_state_locked();
        for (auto &r : roi)
            if (r.active)
                clear_roi_hist_locked(r);
    }
    void configure(int hz, int window_ms) {
        std::lock_guard<std::mutex> l(m);
        max_hz   = std::max(50, std::min(5000, hz));
        n_slices = std::max(2, std::min(100, window_ms / 100));
        burst_us = std::max<int64_t>(50, 250000 / max_hz);
        if (nactive.load() > 0)
            alloc_state_locked();
        for (auto &r : roi)
            if (r.active)
                clear_roi_hist_locked(r);
    }
    void set_roi(int idx, int x0, int y0, int x1, int y1) {
        if (idx < 0 || idx >= kMaxRoi)
            return;
        std::lock_guard<std::mutex> l(m);
        Roi &r = roi[idx];
        x0 = std::max(0, x0);
        y0 = std::max(0, y0);
        x1 = std::min(W, x1);
        y1 = std::min(H, y1);
        if (x1 <= x0 || y1 <= y0) {
            r.active = false;
            r.ring.clear();
            r.slice_id.clear();
        } else {
            r.active = true;
            r.x0 = x0; r.y0 = y0; r.x1 = x1; r.y1 = y1;
            clear_roi_hist_locked(r);
        }
        int n = 0;
        for (auto &q : roi)
            n += q.active ? 1 : 0;
        if (n > 0 && last_start[0].empty())
            alloc_state_locked();
        nactive = n;
    }
    // caller holds m
    void add_sample_locked(Roi &r, int64_t t, double f) {
        const int64_t sid = t / slice_us;
        const int slot = static_cast<int>(((sid % n_slices) + n_slices) % n_slices);
        if (r.slice_id[slot] != sid) {
            std::fill(r.ring.begin() + static_cast<size_t>(slot) * bins(), r.ring.begin() + static_cast<size_t>(slot + 1) * bins(), 0.f);
            r.slice_id[slot] = sid;
        }
        int bin = static_cast<int>(f + 0.5);
        if (bin < 1 || bin > max_hz)
            return;
        r.ring[static_cast<size_t>(slot) * bins() + bin] += 1.f;
    }
    // caller holds m
    void process_locked(const Metavision::EventCD &e) {
        if (e.x >= W || e.y >= H)
            return;
        int hit[kMaxRoi];
        int nh = 0;
        for (int k = 0; k < kMaxRoi; ++k) {
            const Roi &r = roi[k];
            if (r.active && e.x >= r.x0 && e.x < r.x1 && e.y >= r.y0 && e.y < r.y1)
                hit[nh++] = k;
        }
        if (nh == 0)
            return;
        const size_t i = static_cast<size_t>(e.y) * W + e.x;
        const int q    = e.p ? 1 : 0;
        const int64_t t = e.t;
        if (t > latest)
            latest = t;
        const int64_t le = last_ev[q][i];
        if (le == kNever || t - le > burst_us) {
            const int64_t prev = last_start[q][i];
            if (prev != kNever && t > prev) {
                const double f = 1e6 / static_cast<double>(t - prev);
                if (f >= 1.0 && f <= max_hz + 0.5)
                    for (int j = 0; j < nh; ++j)
                        add_sample_locked(roi[hit[j]], t, f);
            }
            last_start[q][i] = t;
        }
        last_ev[q][i] = t;
    }
    // sum of the sliding window; caller holds m
    void window_hist_locked(const Roi &r, std::vector<double> &out) const {
        out.assign(bins(), 0.0);
        if (latest == kNever)
            return;
        const int64_t cur = latest / slice_us;
        for (int s = 0; s < n_slices; ++s) {
            const int64_t id = r.slice_id[s];
            if (id == kNever || id > cur || id <= cur - n_slices)
                continue;
            const float *src = r.ring.data() + static_cast<size_t>(s) * bins();
            for (int b = 0; b < bins(); ++b)
                out[b] += src[b];
        }
    }
};

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

    Analysis an;

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
                std::unique_lock<std::mutex> al(an.m, std::defer_lock);
                if (an.nactive.load() > 0)
                    al.lock();
                const bool use_an = al.owns_lock();
                while (p != e && static_cast<double>(p->t) <= pos) {
                    if (use_an)
                        an.process_locked(*p);
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
        an.reset_state();
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
        p->an.W = p->width;
        p->an.H = p->height;
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

// ------------------------------------------------------------------ vibration analysis API

// max_hz: highest frequency analysed (50..5000, 1 Hz bins); window_ms: sliding window (200..10000).
MVP_API void mvp_an_config(void *h, int max_hz, int window_ms) {
    static_cast<Player *>(h)->an.configure(max_hz, window_ms);
}

// Defines (or with an empty rectangle removes) analysis ROI slot idx (0..7); x1/y1 are exclusive.
MVP_API void mvp_roi_set(void *h, int idx, int x0, int y0, int x1, int y1) {
    static_cast<Player *>(h)->an.set_roi(idx, x0, y0, x1, y1);
}

MVP_API void mvp_roi_clear_all(void *h) {
    Player *p = static_cast<Player *>(h);
    for (int i = 0; i < Analysis::kMaxRoi; ++i)
        p->an.set_roi(i, 0, 0, 0, 0);
}

MVP_API void mvp_an_reset(void *h) {
    static_cast<Player *>(h)->an.reset_state();
}

// Copies the windowed frequency histogram (bin i = i Hz) of ROI idx; returns the bin count or -1.
MVP_API int mvp_roi_hist(void *h, int idx, float *out, int n) {
    Player *p = static_cast<Player *>(h);
    if (idx < 0 || idx >= Analysis::kMaxRoi)
        return -1;
    std::lock_guard<std::mutex> l(p->an.m);
    const Analysis::Roi &r = p->an.roi[idx];
    if (!r.active)
        return -1;
    std::vector<double> hist;
    p->an.window_hist_locked(r, hist);
    const int k = std::min<int>(n, static_cast<int>(hist.size()));
    for (int i = 0; i < k; ++i)
        out[i] = static_cast<float>(hist[i]);
    return k;
}

// Finds up to max_n frequency peaks of ROI idx (strongest first). hz[i] is the refined frequency,
// strength[i] the number of period samples supporting the peak (+-2 Hz).  total = all samples in the
// window.  Returns the peak count, or -1 if the ROI is not active.
MVP_API int mvp_roi_peaks(void *h, int idx, double *hz, double *strength, int max_n, double *total) {
    Player *p = static_cast<Player *>(h);
    if (idx < 0 || idx >= Analysis::kMaxRoi)
        return -1;
    std::vector<double> c;
    {
        std::lock_guard<std::mutex> l(p->an.m);
        const Analysis::Roi &r = p->an.roi[idx];
        if (!r.active)
            return -1;
        p->an.window_hist_locked(r, c);
    }
    const int n = static_cast<int>(c.size());
    double tot = 0;
    for (double v : c)
        tot += v;
    if (total)
        *total = tot;
    std::vector<double> sm(n, 0.0);
    double smax = 0;
    for (int i = 3; i < n - 1; ++i) {
        sm[i] = (c[i - 1] + 2 * c[i] + c[i + 1]) / 4.0;
        smax  = std::max(smax, sm[i]);
    }
    struct Pk { double hz, str, sm; };
    std::vector<Pk> pk;
    for (int i = 3; i < n - 1; ++i) {
        if (sm[i] <= 0 || sm[i] < 0.2 * smax || sm[i] < sm[i - 1] || sm[i] <= sm[i + 1])
            continue;
        double sw = 0, sx = 0;
        for (int j = std::max(1, i - 2); j <= std::min(n - 1, i + 2); ++j) {
            sw += c[j];
            sx += c[j] * j;
        }
        if (sw < 15)
            continue;
        pk.push_back({sx / sw, sw, sm[i]});
    }
    std::sort(pk.begin(), pk.end(), [](const Pk &a, const Pk &b) { return a.str > b.str; });
    std::vector<Pk> sel;
    for (const Pk &q : pk) {
        bool dup = false;
        for (const Pk &s2 : sel)
            if (std::fabs(s2.hz - q.hz) < 3.5)
                dup = true;
        if (!dup)
            sel.push_back(q);
    }
    const int k = std::min<int>(max_n, static_cast<int>(sel.size()));
    for (int i = 0; i < k; ++i) {
        hz[i]       = sel[i].hz;
        strength[i] = sel[i].str;
    }
    return k;
}
