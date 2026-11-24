// Headless end-to-end check of the player library (open, play, pause, speed, seek, loop, end).
#include <chrono>
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
        std::printf("usage: mvp_selftest file.raw\n");
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
    std::printf("ALL OK\n");
    return 0;
}
