using NTwain.Properties;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using System.Windows.Threading;

namespace NTwain.Internals
{
    /// <summary>
    /// This is the self-hosted message loop for TWAIN communication.
    /// It utilizes the wpf Dispatcher to do all the hard work.
    /// </summary>
    sealed class InternalMessageLoopHook : MessageLoopHook
    {
        Dispatcher _dispatcher;
        WindowsHook _hook;
        readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();
        int _drainPosted;

        internal override void Stop()
        {
            if (_dispatcher != null)
            {
                _dispatcher.InvokeShutdown();
            }
        }
        internal override void Start(IWinMessageFilter filter)
        {
            if (_dispatcher == null)
            {
                // using this hack so the new thread will start running before this function returns
                using (var hack = new WrappedManualResetEvent())
                {
                    var loopThread = new Thread(new ThreadStart(() =>
                    {
                        PlatformInfo.Current.Log.Debug("NTwain internal message loop is starting.");
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        if (!PlatformInfo.Current.IsOnMono)
                        {
                            _hook = new WindowsHook(filter);
                            Handle = _hook.Handle;
                        }
                        hack.Set();
                        Dispatcher.Run();
                        // if dispatcher shutsdown we'll get here so make everything uninitialized
                        _dispatcher = null;
                        if (_hook != null)
                        {
                            _hook.Dispose();
                            _hook = null;
                            Handle = IntPtr.Zero;
                        }
                    }));
                    loopThread.IsBackground = true;
                    loopThread.SetApartmentState(ApartmentState.STA);
                    loopThread.Start();
                    hack.Wait();
                }
            }
        }

        public override void BeginInvoke(Action action)
        {
            if (_dispatcher == null) { throw new InvalidOperationException(Resources.MsgLoopUnavailble); }

            // 驱动回调可能在一次传输里连续进来。若每次都 PostMessage，
            // 调度线程正堵在 DSM 调用里时队列会满，并抛出“配额不足”。
            _pending.Enqueue(action);
            if (Interlocked.CompareExchange(ref _drainPosted, 1, 0) != 0)
                return;

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));
            }
            catch (Win32Exception)
            {
                Interlocked.Exchange(ref _drainPosted, 0);
                if (_dispatcher.CheckAccess())
                    Drain();
            }
        }

        void Drain()
        {
            Interlocked.Exchange(ref _drainPosted, 0);
            Action action;
            while (_pending.TryDequeue(out action))
            {
                action();
            }

            if (!_pending.IsEmpty && Interlocked.CompareExchange(ref _drainPosted, 1, 0) == 0)
            {
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));
                }
                catch (Win32Exception)
                {
                    Interlocked.Exchange(ref _drainPosted, 0);
                }
            }
        }

        public override void Invoke(Action action)
        {
            if (_dispatcher == null) { throw new InvalidOperationException(Resources.MsgLoopUnavailble); }

            Exception error = null;

            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else if (PlatformInfo.Current.IsOnMono)
            {
                using (var man = new WrappedManualResetEvent())
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex) { error = ex; }
                        finally
                        {
                            man.Set();
                        }
                    }));
                    man.Wait();
                }
            }
            else
            {
                _dispatcher.Invoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex) { error = ex; }
                }));
            }

            if (error != null) { Rethrow(error); }
        }
    }
}
