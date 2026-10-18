using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 音效编辑器：波形预览 + 选区裁剪 + 导出 WAV（支持把 mp3 转成 16-bit PCM WAV，方便音效库统一格式）。
/// 解码、裁剪、试听全部走 NAudio，不依赖外部工具。
/// </summary>
public partial class SoundEditorWindow : Window
{
    private readonly SoundItem _sound;
    private readonly string _path;
    private long _totalSamples;
    private int _sampleRate;
    private int _channels;
    private float[] _envMin = Array.Empty<float>();
    private float[] _envMax = Array.Empty<float>();
    private int _envBuckets;
    private WaveOutEvent? _preview;
    private AudioFileReader? _previewReader;

    public SoundEditorWindow(SoundItem sound)
    {
        _sound = sound;
        _path = sound.FilePath;
        InitializeComponent();
        FileNameText.Text = sound.DisplayName;
        Loaded += (_, _) => LoadWaveform();
    }

    private void LoadWaveform()
    {
        try
        {
            using var reader = new AudioFileReader(_path);
            _sampleRate = reader.WaveFormat.SampleRate;
            _channels = Math.Max(1, reader.WaveFormat.Channels);
            _totalSamples = reader.Length / reader.WaveFormat.BlockAlign;
            if (_totalSamples <= 0)
            {
                StatusText.Text = L10n.Get("SpEditorEmpty");
                return;
            }

            MetaText.Text = $"{_sampleRate} Hz / {_channels} ch / {FormatTime(_totalSamples)}";

            _envBuckets = 1000;
            _envMin = new float[_envBuckets];
            _envMax = new float[_envBuckets];
            for (int i = 0; i < _envBuckets; i++)
            {
                _envMin[i] = 1f;
                _envMax[i] = -1f;
            }

            int block = 4096 * _channels;
            var buf = new float[block];
            long frame = 0;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int frames = read / _channels;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < _channels; c++)
                    {
                        sum += buf[f * _channels + c];
                    }
                    float mono = sum / _channels;
                    int b = (int)(frame * _envBuckets / _totalSamples);
                    if (b < 0)
                    {
                        b = 0;
                    }
                    else if (b >= _envBuckets)
                    {
                        b = _envBuckets - 1;
                    }
                    if (mono < _envMin[b])
                    {
                        _envMin[b] = mono;
                    }
                    if (mono > _envMax[b])
                    {
                        _envMax[b] = mono;
                    }
                    frame++;
                }
            }

            long ticks = Math.Max(1, _totalSamples / 200);
            StartSlider.Maximum = _totalSamples;
            EndSlider.Maximum = _totalSamples;
            StartSlider.TickFrequency = ticks;
            EndSlider.TickFrequency = ticks;
            StartSlider.Value = 0;
            EndSlider.Value = _totalSamples;

            DrawWaveform();
            UpdateSelection();
            StatusText.Text = L10n.Get("SpEditorHint");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{L10n.Get("SpEditorFail")}: {ex.Message}";
        }
    }

    private void DrawWaveform()
    {
        // 仅移除旧的波形线，保留 4 个叠加层（暗化/选区线）
        for (int i = WaveCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (WaveCanvas.Children[i] is Line)
            {
                WaveCanvas.Children.RemoveAt(i);
            }
        }

        double w = WaveCanvas.ActualWidth;
        double h = WaveCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _envBuckets == 0)
        {
            return;
        }
        double mid = h / 2;
        double scale = mid * 0.92;
        double step = w / _envBuckets;
        var pen = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x82, 0x90));

        for (int b = 0; b < _envBuckets; b++)
        {
            double x = b * step;
            double top = mid - _envMax[b] * scale;
            double bot = mid - _envMin[b] * scale;
            if (top > bot)
            {
                double t = top;
                top = bot;
                bot = t;
            }
            var line = new Line
            {
                X1 = x,
                Y1 = top,
                X2 = x,
                Y2 = bot,
                Stroke = pen,
                StrokeThickness = 1,
            };
            WaveCanvas.Children.Add(line);
        }

        // 把叠加层移到最上层，确保暗化/选区线盖在波形之上
        foreach (var r in new[] { DimStartRect, DimEndRect, StartLine, EndLine })
        {
            WaveCanvas.Children.Remove(r);
            WaveCanvas.Children.Add(r);
        }
    }

    private void UpdateSelection()
    {
        double w = WaveCanvas.ActualWidth;
        double h = WaveCanvas.ActualHeight;
        if (_totalSamples <= 0 || w <= 0)
        {
            return;
        }
        double startX = StartSlider.Value / _totalSamples * w;
        double endX = EndSlider.Value / _totalSamples * w;
        Canvas.SetLeft(DimStartRect, 0);
        DimStartRect.Width = Math.Max(0, startX);
        Canvas.SetLeft(DimEndRect, endX);
        DimEndRect.Width = Math.Max(0, w - endX);

        StartLine.Height = h;
        EndLine.Height = h;
        Canvas.SetLeft(StartLine, startX - 1);
        Canvas.SetTop(StartLine, 0);
        Canvas.SetLeft(EndLine, endX - 1);
        Canvas.SetTop(EndLine, 0);

        long selSamples = (long)(EndSlider.Value - StartSlider.Value);
        SelInfoText.Text = $"{FormatTime((long)StartSlider.Value)} – {FormatTime((long)EndSlider.Value)}" +
            $"  （{L10n.Get("SpEditorSelLen")} {FormatTime(selSamples)}）";
    }

    private void RangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StartSlider.Value > EndSlider.Value - 1)
        {
            // 保持 start < end（至少差 1 个样本）
            if (ReferenceEquals(sender, StartSlider))
            {
                StartSlider.Value = Math.Max(0, EndSlider.Value - 1);
            }
            else
            {
                EndSlider.Value = Math.Min(EndSlider.Maximum, StartSlider.Value + 1);
            }
        }
        UpdateSelection();
    }

    private void WaveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WaveCanvas.ActualWidth > 0 && WaveCanvas.ActualHeight > 0)
        {
            DrawWaveform();
            UpdateSelection();
        }
    }

    private bool TryCreateTrimmed(out ISampleProvider? provider, out AudioFileReader? reader)
    {
        provider = null;
        reader = null;
        if (_totalSamples <= 0)
        {
            return false;
        }
        long start = (long)Math.Min(StartSlider.Value, EndSlider.Value);
        long end = (long)Math.Max(StartSlider.Value, EndSlider.Value);
        long len = end - start;
        if (len <= 0)
        {
            return false;
        }

        AudioFileReader? audio = null;
        try
        {
            audio = new AudioFileReader(_path);
            provider = new OffsetSampleProvider(audio)
            {
                SkipOver = TimeSpan.FromSeconds((double)start / _sampleRate),
                Take = TimeSpan.FromSeconds((double)len / _sampleRate),
            };
            reader = audio; // 生命周期交给调用方（StopPreview 释放）
            return true;
        }
        catch
        {
            audio?.Dispose();
            return false;
        }
    }

    private void PreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        if (!TryCreateTrimmed(out var provider, out var reader) || provider is null)
        {
            StatusText.Text = L10n.Get("SpEditorFail");
            return;
        }
        _previewReader = reader;
        try
        {
            _preview = new WaveOutEvent();
            _preview.Init(new SampleToWaveProvider16(provider));
            _preview.Play();
            StopPreviewBtn.IsEnabled = true;
            StatusText.Text = L10n.Get("SpEditorPreviewing");
        }
        catch (Exception ex)
        {
            StopPreview();
            StatusText.Text = $"{L10n.Get("SpEditorFail")}: {ex.Message}";
        }
    }

    private void StopPreviewBtn_Click(object sender, RoutedEventArgs e) => StopPreview();

    private void StopPreview()
    {
        if (_preview is { } p)
        {
            try
            {
                p.Stop();
                p.Dispose();
            }
            catch
            {
                // 忽略
            }
            _preview = null;
        }
        // 释放试听用的解码器（每次试听都会新建 AudioFileReader，不释放会累积文件句柄）
        _previewReader?.Dispose();
        _previewReader = null;
        StopPreviewBtn.IsEnabled = false;
    }

    private void ExportSelBtn_Click(object sender, RoutedEventArgs e) => ExportSelection(false);

    private void ExportWholeBtn_Click(object sender, RoutedEventArgs e) => ExportSelection(true);

    private void ExportSelection(bool whole)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV (*.wav)|*.wav",
            FileName = whole
                ? System.IO.Path.GetFileNameWithoutExtension(_path) + "_copy.wav"
                : System.IO.Path.GetFileNameWithoutExtension(_path) + "_trim.wav",
            DefaultExt = ".wav",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        try
        {
            long start = whole ? 0 : (long)Math.Min(StartSlider.Value, EndSlider.Value);
            long end = whole ? _totalSamples : (long)Math.Max(StartSlider.Value, EndSlider.Value);
            long len = end - start;
            if (len <= 0)
            {
                StatusText.Text = L10n.Get("SpEditorEmpty");
                return;
            }
            using var reader = new AudioFileReader(_path);
            var trimmed = new OffsetSampleProvider(reader)
            {
                SkipOver = TimeSpan.FromSeconds((double)start / _sampleRate),
                Take = TimeSpan.FromSeconds((double)len / _sampleRate),
            };
            WaveFileWriter.CreateWaveFile(dlg.FileName, new SampleToWaveProvider16(trimmed));
            StatusText.Text = $"{L10n.Get("SpEditorExported")}: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{L10n.Get("SpEditorFail")}: {ex.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPreview();
        base.OnClosed(e);
    }

    private string FormatTime(long samples)
    {
        double sec = _sampleRate > 0 ? samples / (double)_sampleRate : 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalMinutes >= 1
            ? $"{(int)t.TotalMinutes}:{t.Seconds:D2}"
            : $"{sec:F1}s";
    }
}
