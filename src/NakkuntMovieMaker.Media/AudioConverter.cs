using FFmpeg.AutoGen.Abstractions;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Media;

/// <summary>
/// 素材の音声をデコードして、48kHz・float32 の PCM キャッシュに書き出す。
/// モノラルはモノラルのまま、2 チャンネル以上はステレオにまとめる。
/// 時刻の基準は VideoDecoder と同じ（映像があれば映像の先頭）にそろえるので、
/// 音声が映像より遅れて始まる素材は、先頭に無音を入れて位置を合わせる
/// </summary>
internal sealed unsafe class AudioConverter : IDisposable
{
    /// <summary>PCM キャッシュのサンプルレート</summary>
    public const int SampleRate = 48000;

    // タイムスタンプがこれ以上飛んだら、その間を無音で埋める（細かいずれは丸めの誤差として無視する）
    private const long GapThreshold = SampleRate / 10;

    private readonly int _streamIndex;
    private readonly Rational _timeBase;
    private readonly Rational _origin;
    private readonly int _channels;

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private (int Rate, int Format, int Channels, AVChannelOrder Order, ulong Mask) _resamplerInput;
    private float[] _buffer = [];
    private PcmFileWriter? _writer;
    private bool _started;

    // 次に出てくるサンプルの、素材の時刻（サンプル単位）での位置。負の間は基準より前なので捨てる
    private long _position;

    private AudioConverter(string path)
    {
        try
        {
            AVFormatContext* format = null;
            var opened = ffmpeg.avformat_open_input(&format, path, null, null);
            if (opened < 0)
            {
                throw new MediaException($"メディアファイルとして開けません（{FfmpegErrors.Describe(opened)}）: {path}");
            }

            _format = format;
            ffmpeg.avformat_find_stream_info(_format, null).ThrowIfError("ストリームの情報の取得");

            AVCodec* decoder = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (_streamIndex < 0 || decoder is null)
            {
                throw new MediaException($"音声が含まれていないか、デコードできない形式です: {path}");
            }

            var stream = _format->streams[_streamIndex];
            if (stream->time_base.num <= 0 || stream->time_base.den <= 0)
            {
                throw new MediaException($"音声のタイムベースが正しくありません: {path}");
            }

            _timeBase = new Rational(stream->time_base.num, stream->time_base.den);
            _origin = FindOrigin(stream);

            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar).ThrowIfError("デコーダーの設定");
            _codec->pkt_timebase = stream->time_base;
            ffmpeg.avcodec_open2(_codec, decoder, null).ThrowIfError("デコーダーの準備");
            _channels = _codec->ch_layout.nb_channels == 1 ? 1 : 2;

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>sourcePath の音声を、destinationPath（まだ存在しないこと）に PCM キャッシュとして書き出す</summary>
    /// <exception cref="MediaException">ファイルを開けない、または音声が含まれていない</exception>
    public static void Convert(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new MediaException($"ファイルが見つかりません: {sourcePath}");
        }

        FfmpegLibrary.Load();
        using var converter = new AudioConverter(sourcePath);
        converter.Run(destinationPath, cancellationToken);
    }

    private void Run(string destinationPath, CancellationToken cancellationToken)
    {
        using var writer = new PcmFileWriter(destinationPath, SampleRate, _channels);
        _writer = writer;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ffmpeg.av_read_frame(_format, _packet) < 0)
            {
                // ファイルの終わり。デコーダーに残っているフレームを出し切る
                ffmpeg.avcodec_send_packet(_codec, null);
                ReceiveFrames();
                break;
            }

            if (_packet->stream_index == _streamIndex)
            {
                // 壊れたパケットは、その部分だけ飛ばして続ける
                if (ffmpeg.avcodec_send_packet(_codec, _packet) >= 0)
                {
                    ReceiveFrames();
                }
            }

            ffmpeg.av_packet_unref(_packet);
        }

        FlushResampler();
        writer.Complete();
        _writer = null;
    }

    private void ReceiveFrames()
    {
        while (ffmpeg.avcodec_receive_frame(_codec, _frame) == 0)
        {
            try
            {
                Process(_frame);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private void Process(AVFrame* frame)
    {
        if (frame->nb_samples <= 0)
        {
            return;
        }

        EnsureResampler(frame);
        Align(frame);

        var capacity = ffmpeg.swr_get_out_samples(_resampler, frame->nb_samples).ThrowIfError("音声の変換");
        var output = Resample(frame->extended_data, frame->nb_samples, capacity);
        Emit(output);
    }

    // フレームのタイムスタンプと、これまでに書いた位置を比べて、最初の位置合わせと途中の空白の穴埋めをする。
    // 時刻が重なる（戻る）フレームはそのままつなげる
    private void Align(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
        {
            _started = true;
            return;
        }

        var expected = ((timestamp * _timeBase - _origin) * SampleRate).ToInt64(Rounding.Nearest);
        if (!_started)
        {
            _started = true;
            _position = expected;
            if (expected > 0)
            {
                _writer!.WriteSilence(expected);
            }

            return;
        }

        var pending = _position + ffmpeg.swr_get_delay(_resampler, SampleRate);
        if (expected - pending > GapThreshold)
        {
            // 変換の途中のサンプルを出し切ってから、空白を無音で埋める
            FlushResampler();
            EnsureResampler(frame);
            var gap = expected - _position;
            WriteSilence(gap);
        }
    }

    private void WriteSilence(long frames)
    {
        var skipped = Math.Clamp(-_position, 0, frames);
        _writer!.WriteSilence(frames - skipped);
        _position += frames;
    }

    // 変換したサンプルを書く。基準より前（位置が負）のサンプルは捨てる
    private void Emit(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        var skipped = (int)Math.Clamp(-_position, 0, frames);
        _writer!.Write(samples[(skipped * _channels)..]);
        _position += frames;
    }

    private ReadOnlySpan<float> Resample(byte** input, int inputFrames, int capacity)
    {
        if (capacity <= 0)
        {
            return [];
        }

        if (_buffer.Length < capacity * _channels)
        {
            _buffer = new float[capacity * _channels];
        }

        int converted;
        fixed (float* buffer = _buffer)
        {
            var output = (byte*)buffer;
            converted = ffmpeg.swr_convert(_resampler, &output, capacity, input, inputFrames).ThrowIfError("音声の変換");
        }

        return _buffer.AsSpan(0, converted * _channels);
    }

    // 入力の形式（サンプルレート・サンプルの形式・チャンネル）に合わせて変換器を用意する。途中で形式が変わったら作り直す
    private void EnsureResampler(AVFrame* frame)
    {
        var layout = frame->ch_layout;
        var input = (frame->sample_rate, frame->format, layout.nb_channels, layout.order, layout.order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE ? layout.u.mask : 0UL);
        if (_resampler is not null && input == _resamplerInput)
        {
            return;
        }

        FlushResampler();

        AVChannelLayout inputLayout;
        if (layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
        {
            ffmpeg.av_channel_layout_default(&inputLayout, layout.nb_channels);
        }
        else
        {
            ffmpeg.av_channel_layout_copy(&inputLayout, &layout).ThrowIfError("音声のチャンネルの設定");
        }

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, _channels);
        try
        {
            SwrContext* resampler = null;
            ffmpeg.swr_alloc_set_opts2(
                &resampler,
                &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                &inputLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                0, null).ThrowIfError("音声の変換の準備");
            _resampler = resampler;
            ffmpeg.swr_init(_resampler).ThrowIfError("音声の変換の準備");
            _resamplerInput = input;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&inputLayout);
            ffmpeg.av_channel_layout_uninit(&outputLayout);
        }
    }

    // 変換器に残っているサンプルを出し切って、変換器を捨てる
    private void FlushResampler()
    {
        if (_resampler is null)
        {
            return;
        }

        while (ffmpeg.swr_get_out_samples(_resampler, 0) is var capacity and > 0)
        {
            var output = Resample(null, 0, capacity);
            if (output.IsEmpty)
            {
                break;
            }

            Emit(output);
        }

        var resampler = _resampler;
        ffmpeg.swr_free(&resampler);
        _resampler = null;
    }

    // 時刻の基準。VideoDecoder と同じ映像ストリームがあれば（ジャケット画像を除く）その先頭、なければ音声の先頭
    private Rational FindOrigin(AVStream* audio)
    {
        var video = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (video >= 0)
        {
            var stream = _format->streams[video];
            if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0
                && stream->start_time != ffmpeg.AV_NOPTS_VALUE
                && stream->time_base.num > 0 && stream->time_base.den > 0)
            {
                return stream->start_time * new Rational(stream->time_base.num, stream->time_base.den);
            }
        }

        return audio->start_time == ffmpeg.AV_NOPTS_VALUE ? Rational.Zero : audio->start_time * _timeBase;
    }

    public void Dispose()
    {
        if (_resampler is not null)
        {
            var resampler = _resampler;
            ffmpeg.swr_free(&resampler);
            _resampler = null;
        }

        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_codec is not null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format is not null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }
}
