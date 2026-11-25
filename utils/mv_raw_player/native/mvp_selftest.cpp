// Headless end-to-end check of the player library (open, play, pause, speed, seek, loop, end).
#include <chrono>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <thread>
#include <vector>

extern "C" {
const char *mvp_last_error();
void *mvp_open(const char *, int *, int *, int64_t *, int64_t *);
void mvp_close(void *);
void mvp_set_playing(void *, int);
int mvp_is_playing(void *);
void mvp_set_speed(void *, double);
void mvp_set_loop(void *, int);
void mvp_set_accumulation_us(void *, int64_t);
int mvp_is_ended(void *);
int mvp_seek(void *, int64_t);
int64_t mvp_get_position_us(void *);
void mvp_render_bgra(void *, uint8_t *, int);
void mvp_an_config(void *, int, int);
void mvp_roi_set(void *, int, int, int, int, int);
void mvp_roi_clear_all(void *);
int mvp_roi_peaks(void *, int, double *, double *, int, double *);
int mvp_roi_amp(void *, int, double, double *);
int mvp_roi_series(void *, int, int, int, float *, float *, double *);
}

static int lit(const std::vector<uint8_t> &f) {
    int n = 0;
    for (size_t i = 0; i + 3 < f.size(); i += 4)
        if (f[i] != 30 || f[i + 1] != 26) ++n;
    return n;
}
static void sleep_ms(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

#define CHECK(cond, msg)                                    \
    do {                                                    \
        if (!(cond)) {                                      \
            std::printf("FAIL: %s\n", msg);                 \
            return 1;                                       \
        }                                                   \
        std::printf("ok: %s\n", msg);                       \
    } while (0)

int main(int argc, char **argv) {
    if (argc < 2) {
        std::printf("usage: mvp_selftest file.raw [vibration.raw]\n");
        return 2;
    }
    int w = 0, h = 0;
    int64_t t0 = 0, t1 = 0;
    void *p = mvp_open(argv[1], &w, &h, &t0, &t1);
    if (!p) {
        std::printf("FAIL: open: %s\n", mvp_last_error());
        return 1;
    }
    std::printf("opened %dx%d range [%lld, %lld] us\n", w, h, (long long)t0, (long long)t1);
    CHECK(w > 0 && h > 0 && t1 > t0, "geometry and duration");
    std::vector<uint8_t> fr(static_cast<size_t>(w) * h * 4);

    mvp_set_accumulation_us(p, 30000);
    sleep_ms(1000);
    int64_t pos = mvp_get_position_us(p);
    mvp_render_bgra(p, fr.data(), w * 4);
    std::printf("after 1s at 1x: pos=%lld lit=%d\n", (long long)pos, lit(fr));
    CHECK(pos > t0 + 700000 && pos < t0 + 1400000, "1x playback advances ~1s");
    CHECK(lit(fr) > 50, "frame has events");

    mvp_set_playing(p, 0);
    sleep_ms(100);
    int64_t pa = mvp_get_position_us(p);
    sleep_ms(500);
    int64_t pb = mvp_get_position_us(p);
    CHECK(pb - pa < 5000, "pause holds position");
    mvp_set_playing(p, 1);

    mvp_set_speed(p, 4.0);
    int64_t s0 = mvp_get_position_us(p);
    sleep_ms(500);
    int64_t s1 = mvp_get_position_us(p);
    std::printf("4x for 0.5s moved %lld us\n", (long long)(s1 - s0));
    CHECK(s1 - s0 > 1500000 && s1 - s0 < 2500000, "4x speed");

    mvp_set_speed(p, 1.0);
    mvp_set_playing(p, 0);
    CHECK(mvp_seek(p, t0 + 3000000), "seek returns");
    sleep_ms(300);
    pos = mvp_get_position_us(p);
    mvp_render_bgra(p, fr.data(), w * 4);
    std::printf("after seek(3s) paused: pos=%lld lit=%d\n", (long long)pos, lit(fr));
    CHECK(pos >= t0 + 2990000 && pos <= t0 + 3010000, "seek position (paused)");
    CHECK(lit(fr) > 20, "frame visible after seek while paused");

    mvp_set_loop(p, 0);
    mvp_set_speed(p, 20.0);
    mvp_set_playing(p, 1);
    for (int i = 0; i < 60 && !mvp_is_ended(p); ++i) sleep_ms(100);
    CHECK(mvp_is_ended(p), "reaches end without loop");
    CHECK(!mvp_is_playing(p), "stops playing at end");

    mvp_set_loop(p, 1);
    mvp_set_playing(p, 1); // restarts from beginning when ended
    sleep_ms(500);
    pos = mvp_get_position_us(p);
    std::printf("after replay: pos=%lld\n", (long long)pos);
    CHECK(!mvp_is_ended(p) && pos < t0 + 15000000, "replay after end");
    mvp_set_speed(p, 10.0);
    int64_t last = pos;
    bool wrapped = false;
    for (int i = 0; i < 80; ++i) {
        sleep_ms(100);
        int64_t q = mvp_get_position_us(p);
        if (q + 2000000 < last) { wrapped = true; break; }
        last = q;
    }
    CHECK(wrapped, "loop wraps to start");

    mvp_close(p);

    if (argc > 2) {
        // ---- ROI vibration analysis on the synthetic 40 Hz / 50 Hz recording ----
        int vw = 0, vh = 0;
        int64_t v0 = 0, v1 = 0;
        void *v = mvp_open(argv[2], &vw, &vh, &v0, &v1);
        if (!v) {
            std::printf("FAIL: open vibration file: %s\n", mvp_last_error());
            return 1;
        }
        mvp_set_loop(v, 0);
        mvp_set_playing(v, 0);
        mvp_seek(v, v0);
        mvp_an_config(v, 500, 1000);
        mvp_roi_set(v, 0, 140, 90, 180, 140);  // bar A: 40 Hz
        mvp_roi_set(v, 1, 460, 290, 500, 340); // bar B: 50 Hz
        mvp_roi_set(v, 2, 0, 0, vw, vh);       // whole sensor: both
        mvp_roi_set(v, 3, 300, 200, 360, 260); // empty area
        mvp_set_speed(v, 40.0);
        mvp_set_playing(v, 1);
        for (int i = 0; i < 300 && !mvp_is_ended(v); ++i) sleep_ms(100);
        CHECK(mvp_is_ended(v), "vibration file played to the end");

        double hz[8], st[8], total = 0;
        int n = mvp_roi_peaks(v, 0, hz, st, 8, &total);
        std::printf("ROI0: %d peaks, total=%.0f, first=%.2f Hz\n", n, total, n > 0 ? hz[0] : -1.0);
        CHECK(n >= 1 && std::fabs(hz[0] - 40.0) < 1.5, "ROI0 dominant frequency is 40 Hz");

        n = mvp_roi_peaks(v, 1, hz, st, 8, &total);
        std::printf("ROI1: %d peaks, total=%.0f, first=%.2f Hz\n", n, total, n > 0 ? hz[0] : -1.0);
        CHECK(n >= 1 && std::fabs(hz[0] - 50.0) < 1.5, "ROI1 dominant frequency is 50 Hz");

        double am[8];
        int nu = mvp_roi_amp(v, 0, 40.0, am);
        std::printf("ROI0 amp: used=%d Ax=%.2f Ay=%.2f f=%.2f R2x=%.2f\n", nu, am[0], am[1], am[7], am[5]);
        CHECK(nu > 100 && std::fabs(am[0] - 10.0) < 1.5 && am[1] < 1.0, "ROI0 amplitude is ~10 px along X");
        CHECK(std::fabs(am[7] - 40.0) < 0.3, "ROI0 refined frequency is 40.0 Hz");
        nu = mvp_roi_amp(v, 1, 50.3, am);
        std::printf("ROI1 amp: used=%d Ax=%.2f Ay=%.2f f=%.2f R2x=%.2f\n", nu, am[0], am[1], am[7], am[5]);
        CHECK(nu > 100 && std::fabs(am[0] - 10.0) < 1.5 && am[1] < 1.0, "ROI1 amplitude is ~10 px along X");
        CHECK(std::fabs(am[7] - 50.0) < 0.3, "ROI1 refined frequency is 50.0 Hz (from a 0.3 Hz off start)");
        CHECK(mvp_roi_amp(v, 3, 40.0, am) == 0, "empty ROI has no amplitude");
        {
            std::vector<float> tt(2000), vv(2000);
            double wms = 0;
            int ns = mvp_roi_series(v, 0, 0, 2000, tt.data(), vv.data(), &wms);
            float mn = 1e9f, mxv = -1e9f;
            for (int i = 0; i < ns; ++i) { mn = std::min(mn, vv[i]); mxv = std::max(mxv, vv[i]); }
            std::printf("ROI0 series: %d samples, window %.0f ms, range %.1f..%.1f px\n", ns, wms, mn, mxv);
            CHECK(ns > 100 && (mxv - mn) > 14.0 && (mxv - mn) < 24.0, "ROI0 displacement waveform spans ~20 px peak-to-peak");
        }

        n = mvp_roi_peaks(v, 2, hz, st, 8, &total);
        bool f40 = false, f50 = false;
        for (int i = 0; i < n; ++i) {
            std::printf("ROI2 peak %d: %.2f Hz (%.0f samples)\n", i, hz[i], st[i]);
            if (std::fabs(hz[i] - 40.0) < 1.5) f40 = true;
            if (std::fabs(hz[i] - 50.0) < 1.5) f50 = true;
        }
        CHECK(f40 && f50, "whole-frame ROI shows both 40 Hz and 50 Hz peaks");

        n = mvp_roi_peaks(v, 3, hz, st, 8, &total);
        std::printf("ROI3 (empty): %d peaks, total=%.0f\n", n, total);
        CHECK(n <= 0, "empty ROI has no vibration peak");

        mvp_roi_clear_all(v);
        CHECK(mvp_roi_peaks(v, 0, hz, st, 8, &total) < 0, "ROI removed");
        mvp_close(v);
    }

    std::printf("ALL OK\n");
    return 0;
}
