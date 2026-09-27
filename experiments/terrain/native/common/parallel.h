// A persistent thread pool for code that runs many short parallel passes (the water sim steps several times a frame).
// Starting threads per pass, as erosion's parallel_for does, costs more than a small pass itself.
#pragma once
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace cs {

class ThreadPool {
public:
    /// threads: total threads working on a run, including the caller's.
    explicit ThreadPool(int threads) {
        for (int i = 1; i < threads; i++) workers_.emplace_back([this] { Loop(); });
    }

    ~ThreadPool() {
        {
            std::lock_guard<std::mutex> lk(m_);
            stop_ = true;
        }
        wake_.notify_all();
        for (auto& t : workers_) t.join();
    }

    ThreadPool(const ThreadPool&) = delete;
    ThreadPool& operator=(const ThreadPool&) = delete;

    int Size() const { return (int)workers_.size() + 1; }

    /// Runs body(i) for i in [0, count), spread over the pool and the calling thread. Returns when all are done.
    template <class F>
    void Run(int count, F&& body) {
        if (count <= 0) return;
        if (workers_.empty() || count == 1) {
            for (int i = 0; i < count; i++) body(i);
            return;
        }
        std::function<void(int)> fn = std::ref(body);
        {
            std::lock_guard<std::mutex> lk(m_);
            job_ = &fn;
            count_ = count;
            next_.store(0);
            busy_ = (int)workers_.size();
            generation_++;
        }
        wake_.notify_all();
        Work();
        std::unique_lock<std::mutex> lk(m_);
        done_.wait(lk, [&] { return busy_ == 0; });
        job_ = nullptr;
    }

    /// A sensible pool size for work that runs alongside the game: half the cores (the main and render threads and
    /// the engine's own workers need the rest), at least 1.
    static int DefaultThreads() {
        unsigned n = std::thread::hardware_concurrency();
        return (int)std::clamp(n == 0 ? 2u : n / 2, 1u, 16u);
    }

private:
    void Work() {
        for (int i; (i = next_.fetch_add(1)) < count_;) (*job_)(i);
    }

    void Loop() {
        uint64_t seen = 0;
        for (;;) {
            {
                std::unique_lock<std::mutex> lk(m_);
                wake_.wait(lk, [&] { return stop_ || generation_ != seen; });
                if (stop_) return;
                seen = generation_;
            }
            Work();
            std::lock_guard<std::mutex> lk(m_);
            if (--busy_ == 0) done_.notify_one();
        }
    }

    std::vector<std::thread> workers_;
    std::mutex m_;
    std::condition_variable wake_, done_;
    std::function<void(int)>* job_ = nullptr;
    int count_ = 0;
    std::atomic<int> next_{0};
    int busy_ = 0;
    uint64_t generation_ = 0;
    bool stop_ = false;
};

} // namespace cs
