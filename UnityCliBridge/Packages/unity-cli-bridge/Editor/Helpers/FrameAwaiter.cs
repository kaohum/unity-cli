using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Helpers
{
    public interface IFrameScheduler
    {
        void Subscribe(Action tick);
        void Unsubscribe(Action tick);
        double TimeSinceStartup { get; }
        int FrameCount { get; }
    }

    public sealed class EditorFrameScheduler : IFrameScheduler
    {
        public static readonly EditorFrameScheduler Instance = new EditorFrameScheduler();
        int mFrame;
        Action mTick;
        EditorFrameScheduler() { EditorApplication.update += OnUpdate; }
        void OnUpdate() { mFrame++; mTick?.Invoke(); }
        public void Subscribe(Action tick) => mTick += tick;
        public void Unsubscribe(Action tick) => mTick -= tick;
        public double TimeSinceStartup => EditorApplication.timeSinceStartup;
        public int FrameCount => mFrame;
    }

    public sealed class ManualFrameScheduler : IFrameScheduler
    {
        int mFrame;
        Action mTick;
        public int SubscriberCount => mTick?.GetInvocationList().Length ?? 0;
        public double TimeSinceStartup { get; set; }
        public int FrameCount => mFrame;
        public void Subscribe(Action tick) => mTick += tick;
        public void Unsubscribe(Action tick) => mTick -= tick;
        public void AdvanceFrame() { mFrame++; mTick?.Invoke(); }
    }

    public static class FrameAwaiter
    {
        public static Task NextFrame(IFrameScheduler sched, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var tcs = new TaskCompletionSource<bool>();
            Action tick = null;
            tick = () =>
            {
                sched.Unsubscribe(tick);
                if (ct.IsCancellationRequested) tcs.TrySetCanceled();
                else tcs.TrySetResult(true);
            };
            sched.Subscribe(tick);
            ct.Register(() => { sched.Unsubscribe(tick); tcs.TrySetCanceled(); });
            return tcs.Task;
        }

        public static Task DelayFrames(IFrameScheduler sched, int n, CancellationToken ct)
        {
            if (n <= 0) return Task.CompletedTask;
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var tcs = new TaskCompletionSource<bool>();
            int remaining = n;
            Action tick = null;
            tick = () =>
            {
                if (--remaining <= 0)
                {
                    sched.Unsubscribe(tick);
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled();
                    else tcs.TrySetResult(true);
                }
            };
            sched.Subscribe(tick);
            ct.Register(() => { sched.Unsubscribe(tick); tcs.TrySetCanceled(); });
            return tcs.Task;
        }

        public static Task DelaySeconds(IFrameScheduler sched, double seconds, CancellationToken ct)
        {
            if (seconds <= 0) return Task.CompletedTask;
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var tcs = new TaskCompletionSource<bool>();
            double start = sched.TimeSinceStartup;
            Action tick = null;
            tick = () =>
            {
                if (ct.IsCancellationRequested) { sched.Unsubscribe(tick); tcs.TrySetCanceled(); return; }
                if (sched.TimeSinceStartup - start >= seconds)
                {
                    sched.Unsubscribe(tick);
                    tcs.TrySetResult(true);
                }
            };
            sched.Subscribe(tick);
            ct.Register(() => { sched.Unsubscribe(tick); tcs.TrySetCanceled(); });
            return tcs.Task;
        }
    }
}
