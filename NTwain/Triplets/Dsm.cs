using NTwain.Data;
using NTwain.Internals;
using System;
using System.Runtime.ExceptionServices;

namespace NTwain.Triplets
{
    /// <summary>
    /// Contains the full TWAIN entry signatures through pinvoke.
    /// These are called by all the defined triplets methods in 
    /// this namespace.
    /// </summary>
    static partial class Dsm
    {
        internal const string DSM_ENTRY = "DSM_Entry";
        internal const string LINUX_DSM_PATH = "/usr/local/lib/libtwaindsm.so";
        internal const string WIN_NEW_DSM_NAME = "twaindsm.dll";
        internal const string WIN_OLD_DSM_NAME = "twain_32.dll";

        [HandleProcessCorruptedStateExceptions]
        static ReturnCode Invoke(DataGroups dg, DataArgumentType dat, Message msg, string detail, Func<ReturnCode> call)
        {
            DsmCallTrace.Enter(dg, dat, msg, detail);
            try
            {
                var rc = call();
                DsmCallTrace.Leave(dg, dat, msg, rc, detail);
                return rc;
            }
            catch (Exception ex)
            {
                DsmCallTrace.Fault(dg, dat, msg, detail, ex);
                throw;
            }
        }

        #region wrapped calls

        [HandleProcessCorruptedStateExceptions]
        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            DataGroups dg,
            DataArgumentType dat,
            Message msg,
            ref IntPtr data)
        {
            DsmCallTrace.Enter(dg, dat, msg, null);
            try
            {
                ReturnCode rc;
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { rc = NativeMethods.DsmWinNew(origin, destination, dg, dat, msg, ref data); }
                    else { rc = NativeMethods.DsmWinOld(origin, destination, dg, dat, msg, ref data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    rc = NativeMethods.DsmLinux(origin, destination, dg, dat, msg, ref data);
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
                DsmCallTrace.Leave(dg, dat, msg, rc, null);
                return rc;
            }
            catch (Exception ex)
            {
                DsmCallTrace.Fault(dg, dat, msg, null, ex);
                throw;
            }
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            DataGroups dg,
            DataArgumentType dat,
            Message msg,
            IntPtr data)
        {
            return Invoke(dg, dat, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, dg, dat, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, dg, dat, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, dg, dat, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        [HandleProcessCorruptedStateExceptions]
        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            DataGroups dg,
            DataArgumentType dat,
            Message msg,
            ref DataGroups data)
        {
            DsmCallTrace.Enter(dg, dat, msg, null);
            try
            {
                ReturnCode rc;
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { rc = NativeMethods.DsmWinNew(origin, destination, dg, dat, msg, ref data); }
                    else { rc = NativeMethods.DsmWinOld(origin, destination, dg, dat, msg, ref data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    rc = NativeMethods.DsmLinux(origin, destination, dg, dat, msg, ref data);
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
                DsmCallTrace.Leave(dg, dat, msg, rc, null);
                return rc;
            }
            catch (Exception ex)
            {
                DsmCallTrace.Fault(dg, dat, msg, null, ex);
                throw;
            }
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWAudioInfo data)
        {
            return Invoke(DataGroups.Audio, DataArgumentType.AudioInfo, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Audio, DataArgumentType.AudioInfo, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Audio, DataArgumentType.AudioInfo, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Audio, DataArgumentType.AudioInfo, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            DataArgumentType dat,
            Message msg,
            TWCapability data)
        {
            return Invoke(DataGroups.Control, dat, msg, data == null ? null : "cap=" + data.Capability, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, dat, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, dat, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, dat, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWCustomDSData data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.CustomDSData, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.CustomDSData, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.CustomDSData, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.CustomDSData, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWDeviceEvent data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.DeviceEvent, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.DeviceEvent, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.DeviceEvent, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.DeviceEvent, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWCallback data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.Callback, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.Callback, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.Callback, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.Callback, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWCallback2 data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.Callback2, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.Callback2, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.Callback2, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.Callback2, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            Message msg,
            TWEntryPoint data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.EntryPoint, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, null, DataGroups.Control, DataArgumentType.EntryPoint, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, null, DataGroups.Control, DataArgumentType.EntryPoint, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, null, DataGroups.Control, DataArgumentType.EntryPoint, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWEvent data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.Event, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.Event, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.Event, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.Event, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWFileSystem data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.FileSystem, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.FileSystem, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.FileSystem, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.FileSystem, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            Message msg,
            TWIdentity data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.Identity, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, IntPtr.Zero, DataGroups.Control, DataArgumentType.Identity, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, IntPtr.Zero, DataGroups.Control, DataArgumentType.Identity, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, IntPtr.Zero, DataGroups.Control, DataArgumentType.Identity, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWPassThru data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.PassThru, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.PassThru, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.PassThru, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.PassThru, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWPendingXfers data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.PendingXfers, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.PendingXfers, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.PendingXfers, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.PendingXfers, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWSetupFileXfer data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.SetupFileXfer, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.SetupFileXfer, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.SetupFileXfer, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.SetupFileXfer, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWSetupMemXfer data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.SetupMemXfer, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.SetupMemXfer, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.SetupMemXfer, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.SetupMemXfer, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWStatusUtf8 data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.StatusUtf8, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.StatusUtf8, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.StatusUtf8, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.StatusUtf8, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWUserInterface data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.UserInterface, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.UserInterface, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.UserInterface, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.UserInterface, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWCieColor data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.CieColor, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.CieColor, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.CieColor, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.CieColor, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWExtImageInfo data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.ExtImageInfo, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.ExtImageInfo, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.ExtImageInfo, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.ExtImageInfo, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWFilter data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.Filter, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.Filter, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.Filter, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.Filter, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWGrayResponse data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.GrayResponse, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.GrayResponse, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.GrayResponse, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.GrayResponse, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWImageInfo data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.ImageInfo, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.ImageInfo, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.ImageInfo, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.ImageInfo, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWImageLayout data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.ImageLayout, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.ImageLayout, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.ImageLayout, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.ImageLayout, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWImageMemXfer data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.ImageMemXfer, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.ImageMemXfer, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.ImageMemXfer, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.ImageMemXfer, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWJpegCompression data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.JpegCompression, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.JpegCompression, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.JpegCompression, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.JpegCompression, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWPalette8 data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.Palette8, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.Palette8, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.Palette8, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.Palette8, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWRgbResponse data)
        {
            return Invoke(DataGroups.Image, DataArgumentType.RgbResponse, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Image, DataArgumentType.RgbResponse, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Image, DataArgumentType.RgbResponse, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Image, DataArgumentType.RgbResponse, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            Message msg,
            TWStatus data)
        {
            return Invoke(DataGroups.Control, DataArgumentType.Status, msg, null, () =>
            {
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { return NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, DataArgumentType.Status, msg, data); }
                    else { return NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, DataArgumentType.Status, msg, data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    return NativeMethods.DsmLinux(origin, destination, DataGroups.Control, DataArgumentType.Status, msg, data);
                }
                throw new PlatformNotSupportedException();
        
            });
        }

        [HandleProcessCorruptedStateExceptions]
        public static ReturnCode DsmEntry(
            TWIdentity origin,
            TWIdentity destination,
            DataArgumentType dat,
            Message msg,
            ref TWMemory data)
        {
            DsmCallTrace.Enter(DataGroups.Control, dat, msg, null);
            try
            {
                ReturnCode rc;
                if (PlatformInfo.Current.IsWindows)
                {
                    if (PlatformInfo.Current.UseNewWinDSM) { rc = NativeMethods.DsmWinNew(origin, destination, DataGroups.Control, dat, msg, ref data); }
                    else { rc = NativeMethods.DsmWinOld(origin, destination, DataGroups.Control, dat, msg, ref data); }
                }
                else if (PlatformInfo.Current.IsLinux)
                {
                    rc = NativeMethods.DsmLinux(origin, destination, DataGroups.Control, dat, msg, ref data);
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
                DsmCallTrace.Leave(DataGroups.Control, dat, msg, rc, null);
                return rc;
            }
            catch (Exception ex)
            {
                DsmCallTrace.Fault(DataGroups.Control, dat, msg, null, ex);
                throw;
            }
        }


        #endregion
    }
}
