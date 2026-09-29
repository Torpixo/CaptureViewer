using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AForge.Video.DirectShow;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace CaptureViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureViewer.txt");
        readonly MMDeviceEnumerator _en = new MMDeviceEnumerator();
        List<MMDevice> _ins = new List<MMDevice>(), _outs = new List<MMDevice>();
        string[] _saved = new string[6];
        bool _loading, _running, _mute, _showStats = true;

        // video
        Thread _thr; volatile bool _run;
        readonly Mat _latest = new Mat(); bool _fresh; readonly object _lk = new object();
        WriteableBitmap _wb; double _capFps; int _rc; readonly Stopwatch _rt = Stopwatch.StartNew(); double _dispFps;

        // audio
        WasapiCapture _acap; WasapiOut _aout; BufferedWaveProvider _buf; VolumeSampleProvider _vol;

        readonly DispatcherTimer _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        public MainWindow()
        {
            InitializeComponent();
            try { var s = File.ReadAllLines(SettingsFile); for (int i = 0; i < s.Length && i < 6; i++) _saved[i] = s[i]; } catch { }
            _loading = true;
            Fill(CmbRes, new List<string> { "3840x2160", "2560x1440", "1920x1080", "1280x720" }, _saved[1], 2);
            Fill(CmbFps, new List<string> { "240", "144", "120", "60", "30" }, _saved[2], 3);
            Fill(CmbFmt, new List<string> { "MJPG", "YUY2" }, _saved[3], 0);
            _loading = false;
            Scan();
            CompositionTarget.Rendering += OnRender;
            _idle.Tick += (s, e) => { if (_running) { Bar.Visibility = Visibility.Hidden; Cursor = Cursors.None; } };
        }

        static void Fill(ComboBox c, List<string> items, string pref, int def = 0)
        {
            c.ItemsSource = items;
            int i = pref == null ? -1 : items.IndexOf(pref);
            c.SelectedIndex = i >= 0 ? i : (items.Count > def ? def : -1);
        }

        // ---------- device detection ----------
        void Scan()
        {
            _loading = true;
            string pv = CmbVideo.SelectedItem as string ?? _saved[0];
            string pi = CmbIn.SelectedItem as string ?? _saved[4];
            string po = CmbOut.SelectedItem as string ?? _saved[5];
            try
            {
                var vids = new FilterInfoCollection(FilterCategory.VideoInputDevice).Cast<FilterInfo>().Select(f => f.Name).ToList();
                Fill(CmbVideo, vids, pv);
            }
            catch (Exception ex) { Fill(CmbVideo, new List<string>(), null); Toast_(ex.Message); }
            _ins = _en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            _outs = _en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            Fill(CmbIn, new[] { "(No audio)", "Default input" }.Concat(_ins.Select(d => d.FriendlyName)).ToList(), pi);
            Fill(CmbOut, new[] { "Default speakers" }.Concat(_outs.Select(d => d.FriendlyName)).ToList(), po);
            _loading = false;
        }

        // ---------- video (DirectShow via OpenCV, MJPG for high fps) ----------
        void StartVideo()
        {
            int idx = CmbVideo.SelectedIndex;
            if (idx < 0) { Toast_("No video device found"); return; }
            var r = ((string)CmbRes.SelectedItem).Split('x');
            int w = int.Parse(r[0]), h = int.Parse(r[1]), fps = int.Parse((string)CmbFps.SelectedItem);
            string fmt = (string)CmbFmt.SelectedItem;
            _run = true;
            _thr = new Thread(() => CaptureLoop(idx, w, h, fps, fmt)) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thr.Start();
        }

        void CaptureLoop(int idx, int w, int h, int fps, string fmt)
        {
            using var cap = new VideoCapture(idx, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened()) { Dispatcher.Invoke(() => { Toast_("Cannot open the video device (is another app using it?)"); StopAll(); }); return; }
            cap.FourCC = fmt; cap.FrameWidth = w; cap.FrameHeight = h; cap.Fps = fps;
            using var f = new Mat();
            var t = Stopwatch.StartNew(); int c = 0;
            while (_run)
            {
                if (!cap.Read(f) || f.Empty()) { Thread.Sleep(2); continue; }
                lock (_lk) { f.CopyTo(_latest); _fresh = true; }
                c++;
                if (t.ElapsedMilliseconds >= 1000) { _capFps = c * 1000.0 / t.ElapsedMilliseconds; c = 0; t.Restart(); }
            }
        }

        void StopVideo() { _run = false; _thr?.Join(1500); _thr = null; }

        void OnRender(object s, EventArgs e)
        {
            lock (_lk)
            {
                if (_fresh && !_latest.Empty())
                {
                    if (_wb == null || _wb.PixelWidth != _latest.Width || _wb.PixelHeight != _latest.Height)
                    {
                        _wb = new WriteableBitmap(_latest.Width, _latest.Height, 96, 96, PixelFormats.Bgr24, null);
                        Screen.Source = _wb;
                    }
                    WriteableBitmapConverter.ToWriteableBitmap(_latest, _wb);
                    _fresh = false; _rc++;
                }
            }
            if (_rt.ElapsedMilliseconds >= 500)
            {
                _dispFps = _rc * 1000.0 / _rt.ElapsedMilliseconds; _rc = 0; _rt.Restart();
                Stats.Visibility = _showStats && _running ? Visibility.Visible : Visibility.Collapsed;
                if (_wb != null) Stats.Text = $"{_wb.PixelWidth}x{_wb.PixelHeight}   capture {_capFps:F1} fps   display {_dispFps:F0} fps";
            }
        }

        // ---------- audio: chosen input device -> chosen output device ----------
        void StartAudio()
        {
            int i = CmbIn.SelectedIndex;
            if (i <= 0) return;
            try
            {
                MMDevice din = i == 1 ? _en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : _ins[i - 2];
                MMDevice dout = CmbOut.SelectedIndex <= 0 ? _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : _outs[CmbOut.SelectedIndex - 1];

                _acap = new WasapiCapture(din, true, 30);
                var cf = _acap.WaveFormat;
                var bf = cf.BitsPerSample == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(cf.SampleRate, cf.Channels)
                                                : new WaveFormat(cf.SampleRate, cf.BitsPerSample, cf.Channels);
                _buf = new BufferedWaveProvider(bf) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(300), ReadFully = true };
                _acap.DataAvailable += (s, e) => _buf?.AddSamples(e.Buffer, 0, e.BytesRecorded);

                ISampleProvider sp = _buf.ToSampleProvider();
                var mix = dout.AudioClient.MixFormat;
                if (sp.WaveFormat.SampleRate != mix.SampleRate) sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
                var mux = new MultiplexingSampleProvider(new[] { sp }, mix.Channels);
                for (int o = 0; o < Math.Min(mix.Channels, 2); o++)
                    mux.ConnectInputToOutput(sp.WaveFormat.Channels == 1 ? 0 : o, o);
                _vol = new VolumeSampleProvider(mux) { Volume = _mute ? 0 : (float)Vol.Value };

                _aout = new WasapiOut(dout, AudioClientShareMode.Shared, true, 40);
                _aout.Init(_vol.ToWaveProvider());
                _acap.StartRecording();
                _aout.Play();
            }
            catch (Exception ex) { Toast_("Audio: " + ex.Message); StopAudio(); }
        }

        void StopAudio()
        {
            try { _acap?.StopRecording(); } catch { }
            try { _aout?.Stop(); } catch { }
            _acap?.Dispose(); _aout?.Dispose();
            _acap = null; _aout = null; _buf = null; _vol = null;
        }

        // ---------- app logic ----------
        void StartAll() { StopAll(); _running = true; StartVideo(); StartAudio(); BtnStart.Content = "Stop"; Hint.Visibility = Visibility.Collapsed; _idle.Start(); }
        void StopAll()
        {
            StopVideo(); StopAudio(); _running = false; _idle.Stop();
            BtnStart.Content = "Start"; Hint.Visibility = Visibility.Visible; Screen.Source = null; _wb = null;
            Bar.Visibility = Visibility.Visible; Cursor = null;
        }

        void Toast_(string m)
        {
            Toast.Text = m; Toast.Visibility = Visibility.Visible;
            Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(() => Toast.Visibility = Visibility.Collapsed));
        }

        void ToggleFull()
        {
            if (WindowStyle == WindowStyle.None) { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal; }
            else { WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        }

        void ToggleMute() { _mute = !_mute; BtnMute.Content = _mute ? "Unmute" : "Mute"; if (_vol != null) _vol.Volume = _mute ? 0 : (float)Vol.Value; }

        void Snapshot()
        {
            lock (_lk)
            {
                if (_latest.Empty()) return;
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                Cv2.ImWrite(p, _latest); Toast_("Saved " + p);
            }
        }

        // ---------- UI events ----------
        void Start_Click(object s, RoutedEventArgs e) { if (_running) StopAll(); else StartAll(); }
        void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();
        void Rescan_Click(object s, RoutedEventArgs e) { Scan(); Toast_($"Found {CmbVideo.Items.Count} video, {_ins.Count} audio input, {_outs.Count} audio output devices"); }
        void Snap_Click(object s, RoutedEventArgs e) => Snapshot();
        void Full_Click(object s, RoutedEventArgs e) => ToggleFull();
        void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (_vol != null && !_mute) _vol.Volume = (float)e.NewValue; }
        void Video_Changed(object s, SelectionChangedEventArgs e) { if (_loading || !_running) return; StopVideo(); StartVideo(); }
        void Audio_Changed(object s, SelectionChangedEventArgs e) { if (_loading || !_running) return; StopAudio(); StartAudio(); }
        void Screen_MouseDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleFull(); }

        void Window_MouseMove(object s, MouseEventArgs e)
        {
            Bar.Visibility = Visibility.Visible; Cursor = null;
            if (_running) { _idle.Stop(); _idle.Start(); }
        }

        void Window_KeyDown(object s, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.F11: case Key.F: ToggleFull(); break;
                case Key.Escape: if (WindowStyle == WindowStyle.None) ToggleFull(); break;
                case Key.M: ToggleMute(); break;
                case Key.S: _showStats = !_showStats; break;
                case Key.P: Snapshot(); break;
            }
        }

        void Window_Closing(object s, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                File.WriteAllLines(SettingsFile, new[] {
                    CmbVideo.SelectedItem as string ?? "", CmbRes.SelectedItem as string ?? "", CmbFps.SelectedItem as string ?? "",
                    CmbFmt.SelectedItem as string ?? "", CmbIn.SelectedItem as string ?? "", CmbOut.SelectedItem as string ?? "" });
            }
            catch { }
            StopAll();
        }
    }
}
