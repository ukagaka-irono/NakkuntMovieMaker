using System.Collections.Concurrent;
using Vortice.Vulkan;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// GPU に送った仕事の完了を表す（タイムラインセマフォの値）。Wait で完了を待つ
/// </summary>
public readonly record struct GpuFence(GpuScheduler Scheduler, ulong Value)
{
    public bool IsCompleted => Scheduler.CompletedValue >= Value;

    public void Wait(CancellationToken cancellationToken = default) => Scheduler.WaitFor(Value, cancellationToken);
}

/// <summary>
/// GPU のスレッドの上で、1 つの仕事のコマンドを記録するときに渡すもの。CommandBuffer に記録する。
/// Flush で、そこまでのコマンドを先に送れる（Skia にそれを使って描かせる前など）。
/// OnCompleted に渡した処理は、この仕事が GPU で終わったあとに GPU のスレッドで呼ばれる（使い終わった資源を捨てるのに使う）
/// </summary>
public sealed unsafe class GpuCommandContext
{
    private readonly GpuScheduler _scheduler;
    private readonly GpuScheduler.Slot _slot;

    internal GpuCommandContext(GpuScheduler scheduler, GpuScheduler.Slot slot, ulong value)
    {
        _scheduler = scheduler;
        _slot = slot;
        Value = value;
        CommandBuffer = slot.Begin(scheduler.Device);
    }

    public VulkanDevice Device => _scheduler.Device;

    public VkCommandBuffer CommandBuffer { get; private set; }

    /// <summary>この仕事が終わったときのタイムラインセマフォの値</summary>
    public ulong Value { get; }

    /// <summary>この仕事が GPU で終わったことを待てるもの</summary>
    public GpuFence Fence => new(_scheduler, Value);

    /// <summary>ここまで記録したコマンドを送り、新しいコマンドバッファーで続ける（完了の合図はまだ出さない）</summary>
    public void Flush()
    {
        _scheduler.Submit(_slot.End(Device), signal: null, _slot);
        CommandBuffer = _slot.Begin(Device);
    }

    public void OnCompleted(Action action) => _slot.Completions.Add(action);

    /// <summary>次に送るコマンドの前に、ほかのタイムラインセマフォ（FFmpeg のデコードしたフレームなど）が value に届くのを待つ</summary>
    public void WaitSemaphore(VkSemaphore semaphore, ulong value, VkPipelineStageFlags stage) => _slot.Waits.Add((semaphore, value, stage));

    /// <summary>この仕事が終わったら、ほかのタイムラインセマフォを value にする</summary>
    public void SignalSemaphore(VkSemaphore semaphore, ulong value) => _slot.Signals.Add((semaphore, value));

    /// <summary>この仕事の間だけ使うディスクリプターセット（記録の場所を使い回すときに、まとめて返す）</summary>
    public VkDescriptorSet AllocateDescriptorSet(VkDescriptorSetLayout layout) => _slot.AllocateDescriptorSet(Device, layout);

    internal void Finish() => _scheduler.Submit(_slot.End(Device), signal: Value, _slot);

    internal GpuScheduler.Slot Slot => _slot;
}

/// <summary>
/// GPU のキューへの送信を 1 本のスレッドにまとめる（Vulkan のキューと Skia の GRContext は、同時に 1 つのスレッドからしか使えない）。
/// 仕事は送った順に記録・送信し、仕事ごとにタイムラインセマフォの値を 1 つずつ進める。同時に GPU で処理する仕事は FramesInFlight 個まで
/// （コマンドを記録する場所を使い回すため、空くまで次の仕事を待たせる）
/// </summary>
public sealed unsafe class GpuScheduler : IDisposable
{
    private readonly BlockingCollection<Job> _jobs = new();
    private readonly Thread _thread;
    private readonly Slot[] _slots;
    private readonly VkSemaphore _timeline;
    private ulong _nextValue;
    private bool _disposed;

    public GpuScheduler(VulkanDevice device, int framesInFlight = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        Device = device;
        var api = device.DeviceApi;
        var type = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var semaphoreInfo = new VkSemaphoreCreateInfo { pNext = &type };
        VkSemaphore semaphore;
        Check(api.vkCreateSemaphore(&semaphoreInfo, null, &semaphore), "タイムラインセマフォの作成");
        _timeline = semaphore;
        _slots = [.. Enumerable.Range(0, framesInFlight).Select(_ => new Slot(device))];
        _thread = new Thread(Run) { IsBackground = true, Name = "GPU" };
        _thread.Start();
    }

    public VulkanDevice Device { get; }

    /// <summary>GPU で終わった仕事のタイムラインセマフォの値</summary>
    public ulong CompletedValue
    {
        get
        {
            ulong value;
            Device.DeviceApi.vkGetSemaphoreCounterValue(_timeline, &value);
            return value;
        }
    }

    /// <summary>今のスレッドが GPU のスレッドか</summary>
    public bool IsGpuThread => Thread.CurrentThread == _thread;

    /// <summary>
    /// GPU のスレッドで record を呼んでコマンドを記録させ、GPU に送る。record の戻り値を返す（GPU での完了は待たない。
    /// 待つときは、記録のときに受け取った GpuFence で待つ）。record が例外を投げたら、何も送らずに例外を返す
    /// </summary>
    public Task<T> RunAsync<T>(Func<GpuCommandContext, T> record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(new Job(context =>
        {
            try
            {
                completion.TrySetResult(record(context));
                return true;
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                return false;
            }
        }, cancellationToken, () => completion.TrySetCanceled(cancellationToken)), CancellationToken.None);
        return completion.Task;
    }

    /// <summary>タイムラインセマフォが value に届くまで待つ</summary>
    public void WaitFor(ulong value, CancellationToken cancellationToken = default)
    {
        var semaphore = _timeline;
        var info = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &semaphore, pValues = &value };
        while (true)
        {
            // キャンセルを見られるように、少しずつ待つ
            var result = Device.DeviceApi.vkWaitSemaphores(&info, 10_000_000);
            if (result == VkResult.Success)
            {
                return;
            }

            if (result != VkResult.Timeout)
            {
                throw new VulkanException($"GPU の処理を待てませんでした（{result}）");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _jobs.CompleteAdding();
        _thread.Join();
        Device.DeviceApi.vkDeviceWaitIdle();
        foreach (var slot in _slots)
        {
            slot.RunCompletions();
            slot.Dispose(Device);
        }

        Device.DeviceApi.vkDestroySemaphore(_timeline, null);
        _jobs.Dispose();
    }

    internal void Submit(VkCommandBuffer commands, ulong? signal, Slot? slot = null)
    {
        // 待つセマフォ（記録の途中で頼まれたもの）と、合図するセマフォ（自分のタイムラインと、最後の送信なら頼まれたもの）
        var waits = slot?.Waits ?? [];
        var signals = new List<(VkSemaphore Semaphore, ulong Value)>();
        if (signal is { } own)
        {
            signals.Add((_timeline, own));
            if (slot is not null)
            {
                signals.AddRange(slot.Signals);
                slot.Signals.Clear();
            }
        }

        var waitSemaphores = stackalloc VkSemaphore[Math.Max(1, waits.Count)];
        var waitValues = stackalloc ulong[Math.Max(1, waits.Count)];
        var waitStages = stackalloc VkPipelineStageFlags[Math.Max(1, waits.Count)];
        for (var i = 0; i < waits.Count; i++)
        {
            (waitSemaphores[i], waitValues[i], waitStages[i]) = waits[i];
        }

        var signalSemaphores = stackalloc VkSemaphore[Math.Max(1, signals.Count)];
        var signalValues = stackalloc ulong[Math.Max(1, signals.Count)];
        for (var i = 0; i < signals.Count; i++)
        {
            (signalSemaphores[i], signalValues[i]) = signals[i];
        }

        var timelineInfo = new VkTimelineSemaphoreSubmitInfo
        {
            waitSemaphoreValueCount = (uint)waits.Count,
            pWaitSemaphoreValues = waitValues,
            signalSemaphoreValueCount = (uint)signals.Count,
            pSignalSemaphoreValues = signalValues,
        };
        var submit = new VkSubmitInfo
        {
            pNext = &timelineInfo,
            waitSemaphoreCount = (uint)waits.Count,
            pWaitSemaphores = waitSemaphores,
            pWaitDstStageMask = waitStages,
            commandBufferCount = 1,
            pCommandBuffers = &commands,
            signalSemaphoreCount = (uint)signals.Count,
            pSignalSemaphores = signalSemaphores,
        };
        lock (Device.QueueLock)
        {
            Check(Device.DeviceApi.vkQueueSubmit(Device.Queue, 1, &submit, VkFence.Null), "GPU への送信");
        }

        slot?.Waits.Clear();
    }

    private void Run()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            if (job.Cancellation.IsCancellationRequested)
            {
                job.Cancel();
                continue;
            }

            // 使い回す記録の場所が空くまで待ち、前に使った仕事の後始末をする
            var value = _nextValue + 1;
            var slot = _slots[value % (ulong)_slots.Length];
            if (slot.LastValue > 0)
            {
                WaitFor(slot.LastValue);
                slot.RunCompletions();
            }

            slot.Reset(Device);
            var context = new GpuCommandContext(this, slot, value);
            if (job.Record(context))
            {
                context.Finish();
                slot.LastValue = value;
                _nextValue = value;
            }
            else
            {
                // 記録に失敗した仕事は送らない（値も進めない）
                slot.End(Device);
                slot.Completions.Clear();
                slot.Waits.Clear();
                slot.Signals.Clear();
            }
        }
    }

    private static void Check(VkResult result, string what)
    {
        if (result != VkResult.Success)
        {
            throw new VulkanException($"{what}に失敗しました（{result}）");
        }
    }

    private sealed record Job(Func<GpuCommandContext, bool> Record, CancellationToken Cancellation, Action Cancel);

    /// <summary>コマンドを記録する場所（コマンドプールと、そこから取ったコマンドバッファー）</summary>
    internal sealed class Slot
    {
        // 1 つの仕事で使うディスクリプターの上限（1 フレームの描画に十分な数）
        private const uint MaxSets = 4096;

        private readonly VkCommandPool _pool;
        private readonly VkDescriptorPool _descriptors;
        private readonly List<VkCommandBuffer> _buffers = [];
        private int _used;
        private VkCommandBuffer _current;

        public Slot(VulkanDevice device)
        {
            var info = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.Transient, queueFamilyIndex = device.QueueFamily };
            VkCommandPool pool;
            Check(device.DeviceApi.vkCreateCommandPool(&info, null, &pool), "コマンドプールの作成");
            _pool = pool;

            var sizes = stackalloc VkDescriptorPoolSize[3];
            sizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = MaxSets * 2 };
            sizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageImage, descriptorCount = MaxSets * 2 };
            sizes[2] = new VkDescriptorPoolSize { type = VkDescriptorType.CombinedImageSampler, descriptorCount = MaxSets * 2 };
            var descriptorInfo = new VkDescriptorPoolCreateInfo { maxSets = MaxSets, poolSizeCount = 3, pPoolSizes = sizes };
            VkDescriptorPool descriptors;
            Check(device.DeviceApi.vkCreateDescriptorPool(&descriptorInfo, null, &descriptors), "ディスクリプタープールの作成");
            _descriptors = descriptors;
        }

        public VkDescriptorSet AllocateDescriptorSet(VulkanDevice device, VkDescriptorSetLayout layout)
        {
            var allocate = new VkDescriptorSetAllocateInfo { descriptorPool = _descriptors, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            Check(device.DeviceApi.vkAllocateDescriptorSets(&allocate, &set), "ディスクリプターの確保");
            return set;
        }

        public ulong LastValue { get; set; }

        public List<Action> Completions { get; } = [];

        /// <summary>次の送信で待つ、ほかのタイムラインセマフォ</summary>
        public List<(VkSemaphore Semaphore, ulong Value, VkPipelineStageFlags Stage)> Waits { get; } = [];

        /// <summary>この仕事の最後の送信で合図する、ほかのタイムラインセマフォ</summary>
        public List<(VkSemaphore Semaphore, ulong Value)> Signals { get; } = [];

        public void Reset(VulkanDevice device)
        {
            Check(device.DeviceApi.vkResetCommandPool(_pool, VkCommandPoolResetFlags.None), "コマンドプールのリセット");
            Check(device.DeviceApi.vkResetDescriptorPool(_descriptors, 0), "ディスクリプタープールのリセット");
            _used = 0;
        }

        public VkCommandBuffer Begin(VulkanDevice device)
        {
            if (_used == _buffers.Count)
            {
                var allocate = new VkCommandBufferAllocateInfo { commandPool = _pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
                VkCommandBuffer buffer;
                Check(device.DeviceApi.vkAllocateCommandBuffers(&allocate, &buffer), "コマンドバッファーの確保");
                _buffers.Add(buffer);
            }

            _current = _buffers[_used++];
            var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            Check(device.DeviceApi.vkBeginCommandBuffer(_current, &begin), "コマンドの記録の開始");
            return _current;
        }

        public VkCommandBuffer End(VulkanDevice device)
        {
            Check(device.DeviceApi.vkEndCommandBuffer(_current), "コマンドの記録の終了");
            return _current;
        }

        public void RunCompletions()
        {
            foreach (var action in Completions)
            {
                action();
            }

            Completions.Clear();
        }

        public void Dispose(VulkanDevice device)
        {
            device.DeviceApi.vkDestroyCommandPool(_pool, null);
            device.DeviceApi.vkDestroyDescriptorPool(_descriptors, null);
        }
    }
}
