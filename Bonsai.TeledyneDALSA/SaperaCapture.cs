using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenCV.Net;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using DALSA.SaperaLT.SapClassBasic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Diagnostics;

namespace Bonsai.TeledyneDALSA
{
    public static class RuntimePolicyHelper
    {
        // Enable legacy V2 runtime (to work properly with the Teledyne DALSA SDK)
        // Taken from: http://reedcopsey.com/2011/09/15/setting-uselegacyv2runtimeactivationpolicy-at-runtime/

        public static bool LegacyV2RuntimeEnabledSuccessfully { get; private set; }

        static RuntimePolicyHelper()
        {
            ICLRRuntimeInfo clrRuntimeInfo =
                (ICLRRuntimeInfo)RuntimeEnvironment.GetRuntimeInterfaceAsObject(
                    Guid.Empty,
                    typeof(ICLRRuntimeInfo).GUID);
            try
            {
                clrRuntimeInfo.BindAsLegacyV2Runtime();
                LegacyV2RuntimeEnabledSuccessfully = true;
            }
            catch (COMException)
            {
                // This occurs with an HRESULT meaning
                // "A different runtime was already bound to the legacy CLR version 2 activation policy."
                LegacyV2RuntimeEnabledSuccessfully = false;
            }
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BD39D1D2-BA2F-486A-89B0-B4B0CB466891")]
        private interface ICLRRuntimeInfo
        {
            void xGetVersionString();
            void xGetRuntimeDirectory();
            void xIsLoaded();
            void xIsLoadable();
            void xLoadErrorString();
            void xLoadLibrary();
            void xGetProcAddress();
            void xGetInterface();
            void xSetDefaultStartupFlags();
            void xGetDefaultStartupFlags();

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void BindAsLegacyV2Runtime();
        }
    }

    // Set the node's description
    [Description("Produces a sequence of images acquired from an Teledyne DALSA camera using the Sapera SDK.")]
    public class SaperaCapture : Source<SaperaDataFrame>
    {
        // Create camera index parameter
        [Description("The index of the camera from which to acquire images.")]
        public int Index { get; set; }

        public override IObservable<SaperaDataFrame> Generate()
        {
            return source;
        }

        // Create exposure time parameter
        private double exposureTime;
        [Description("Exposure time (ms). This controls the maximum framerate.")]
        public double ExposureTime
        {
            get
            {
                return exposureTime;
            }
            set
            {
                exposureTime = Math.Round(value, 2);
            }
        }
        
        // Create frame rate parameter
        private double frameRate;
        [Description("Desired frame rate (frames / s). This is superceded by the maximum framerate allowed by the exposure time. When UseMaxFrameRate is set to True, FrameRate is set to -1.")]
        public double FrameRate
        {
            get
            {
                return frameRate;
            }
            set
            {
                frameRate = Math.Floor(value * 100) / 100;
            }
        }

        // Create black level parameter
        private double blackLevel;
        [Description("Black level (DN). This controls the analog black level as DC offset applied to the video signal.")]
        public double BlackLevel
        {
            get
            {
                return blackLevel;
            }
            set
            {
                blackLevel = Math.Round(value, 2);
            }
        }

        // Create gain parameter
        private double gain;
        [Description("Adjusts the gain (dB). This controls the gain as an amplification factor applied to the video signal.")]
        public double Gain
        {
            get
            {
                return gain;
            }
            set
            {
                gain = Math.Round(value, 2);
            }
        }

        // Create parameter for determining whether to automatically use the maximum possible frame rate for the current exposure time
        private bool useMaxFrameRate;
        [Description("Whether to set the FPS to the maximum possible value based on the current exposure time.")]
        public bool UseMaxFrameRate
        {
            get
            {
                return useMaxFrameRate;
            }
            set
            {
                useMaxFrameRate = value;
            }
        }

        // Create parameter for determining whether to reset the device before acquiring frames
        private bool resetDevice;
        [Description("Resets the camera before acquiring frames.")]
        public bool ResetDevice
        {
            get
            {
                return resetDevice;
            }
            set
            {
                resetDevice = value;
            }
        }


        // Create variables
        IObservable<SaperaDataFrame> source;
        readonly object captureLock = new object();
        IObserver<SaperaDataFrame> global_observer;

        // Function used for destroying and disposing of sapera class objects
        static void DestroyObjects(SapAcqDevice device, SapBuffer buffer, SapTransfer transfer, SapFeature feature)
        {
            if (transfer != null)
            {
                transfer.Destroy();
                transfer.Dispose();
            }

            if (device != null)
            {
                device.Destroy();
                device.Dispose();
            }

            if (buffer != null)
            {
                buffer.Destroy();
                buffer.Dispose();
            }

            if (feature != null)
            {
                feature.Destroy();
                feature.Dispose();
            }
        }

        // Callback function for when a frame is grabbed by the camera
        private void Xfer_XferNotify(object sender, SapXferNotifyEventArgs args)
        {
            IplImage output;
            SapBuffer buffer = args.Context as SapBuffer;
            unsafe
            {
                // Access the buffer data
                buffer.GetAddress(out IntPtr image);
                // Copy the frame into an IplImage object
                output = new IplImage(new Size(buffer.Width, buffer.Height), IplDepth.U8, 1, image).Clone();
            }
            // Send to the next node
            global_observer.OnNext(new SaperaDataFrame(output, buffer.DeviceTimeStamp, buffer.CounterStamp, buffer.HostCounterStamp));
        }

        public SaperaCapture()
        {
            if (RuntimePolicyHelper.LegacyV2RuntimeEnabledSuccessfully)
            {
                source = Observable.Create<SaperaDataFrame>((observer, cancellationToken) =>
                {
                    return Task.Factory.StartNew(() =>
                    {
                        lock (captureLock)
                        {
                            // Initialize variables
                            string serverName = "";
                            SapLocation location = null;                 
                            SapAcqDevice device = null;
                            SapTransfer transfer = null;
                            SapBuffer buffer = null;
                            SapFeature feature = null;
                            double doubleFeatureValue;
                            double maxValue;
                            double minValue;
                            global_observer = observer;

                            // Get server count
                            int serverCount = SapManager.GetServerCount();

                            // Check if a server is available
                            if (serverCount == 0)
                            {
                                Console.WriteLine("No device found.\n");
                                return;
                            }

                            // Check if index variable is valid
                            if (Index >= 0 && Index < serverCount - 1)
                            {
                                // Find the name of the server
                                serverName = SapManager.GetServerName(Index + 1);
                            }
                            else
                            {
                                Console.WriteLine("Invalid index selection.\n");
                                return;
                            }

                            // Find server location
                            location = new SapLocation(serverName, 0);

                            // Find device
                            device = new SapAcqDevice(location, false);

                            // Create buffer
                            buffer = new SapBufferWithTrash(3, device, SapBuffer.MemoryType.ScatterGather);

                            // Initialize transfer between device and buffer
                            transfer = new SapAcqDeviceToBuf(device, buffer);

                            // Check if device was created
                            if (!device.Create())
                            {
                                Console.WriteLine("Error during SapAcqDevice creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Initialize frame handler for end of frame events
                            transfer.Pairs[0].EventType = SapXferPair.XferEventType.EndOfFrame;
                            transfer.XferNotify += new SapXferNotifyHandler(Xfer_XferNotify);
                            transfer.XferNotifyContext = buffer;

                            // Check if buffer was created
                            if (!buffer.Create())
                            {
                                Console.WriteLine("Error during SapBuffer creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Check if transfer layer was created
                            if (!transfer.Create())
                            {
                                Console.WriteLine("Error during SapAcqDeviceToBuf creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Create feature
                            feature = new SapFeature(location);

                            // Check if feature was created
                            if (!feature.Create())
                            {
                                Console.WriteLine("Error during SapFeature creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Check if reset device is true
                            if (resetDevice)
                            {
                                // Check if feature is available
                                if (device.IsFeatureAvailable("DeviceReset"))
                                {
                                    // Reset the device
                                    device.SetFeatureValue("DeviceReset", false);

                                    // Destroy objects
                                    DestroyObjects(device, buffer, transfer, feature);
                                    location.Dispose();

                                    // Check if device is online
                                    while (!SapManager.IsResourceAvailable(serverName, SapManager.ResourceType.AcqDevice, 0))
                                    {
                                        // Do nothing
                                    }

                                    // Create objects again
                                    location = new SapLocation(serverName, 0);
                                    device = new SapAcqDevice(location, false);
                                    buffer = new SapBufferWithTrash(3, device, SapBuffer.MemoryType.ScatterGather);
                                    transfer = new SapAcqDeviceToBuf(device, buffer);
                                    if (!device.Create())
                                    {
                                        Console.WriteLine("Error during SapAcqDevice creation.\n");
                                        DestroyObjects(device, buffer, transfer, feature);
                                        location.Dispose();
                                        return;
                                    }

                                    transfer.Pairs[0].EventType = SapXferPair.XferEventType.EndOfFrame;
                                    transfer.XferNotify += new SapXferNotifyHandler(Xfer_XferNotify);
                                    transfer.XferNotifyContext = buffer;

                                    if (!buffer.Create())
                                    {
                                        Console.WriteLine("Error during SapBuffer creation.\n");
                                        DestroyObjects(device, buffer, transfer, feature);
                                        location.Dispose();
                                        return;
                                    }

                                    if (!transfer.Create())
                                    {
                                        Console.WriteLine("Error during SapAcqDeviceToBuf creation.\n");
                                        DestroyObjects(device, buffer, transfer, feature);
                                        location.Dispose();
                                        return;
                                    }

                                    feature = new SapFeature(location);

                                    if (!feature.Create())
                                    {
                                        Console.WriteLine("Error during SapFeature creation.\n");
                                        DestroyObjects(device, buffer, transfer, feature);
                                        location.Dispose();
                                        return;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("DeviceReset not available.\n");
                                }
                            }

                            // Check if frame rate control is available
                            if (device.IsFeatureAvailable("acquisitionFrameRateControlMode"))
                            {
                                // Check if use max frame rate is true
                                if (UseMaxFrameRate)
                                {
                                    // Set the frame rate to max
                                    device.SetFeatureValue("acquisitionFrameRateControlMode", "MaximumSpeed");
                                }
                                else
                                {
                                    // Set the frame rate to programmable
                                    device.SetFeatureValue("acquisitionFrameRateControlMode", "Programmable");
                                }
                            }
                            else
                            {
                                Console.WriteLine("UseMaxFrameRate not available.\n");
                            }

                            // Check if exposure time is available
                            if (device.IsFeatureAvailable("ExposureTime"))
                            {
                                // Check if exposure has been manually set
                                if (ExposureTime == 0)  
                                {
                                    // Set exposure time variable to current value on the camera (convert from us to ms)
                                    device.GetFeatureValue("ExposureTime", out doubleFeatureValue);
                                    ExposureTime = doubleFeatureValue / 1000.0;
                                }
                                else
                                {
                                    // Gets information about exposure time
                                    device.GetFeatureInfo("ExposureTime", feature);
                                    feature.GetValueMax(out maxValue);
                                    feature.GetValueMin(out minValue);

                                    if (ExposureTime * 1000.0 < maxValue && ExposureTime * 1000.0 > minValue)
                                    {
                                        // Sets camera's exposure time to exposure time variable
                                        device.SetFeatureValue("ExposureTime", ExposureTime * 1000.0);
                                    }
                                    else if (ExposureTime * 1000.0 >= maxValue)
                                    {
                                        // If the exposure time variable is greater than or equal to the maximum exposure time, set the exposure time to the maximum value
                                        device.SetFeatureValue("ExposureTime", maxValue);
                                        ExposureTime = maxValue;
                                    }
                                    else
                                    {
                                        // If the exposure time variable is less than or equal to the minumum exposure time, set the exposure time to the minumum value
                                        device.SetFeatureValue("ExposureTime", minValue);
                                        ExposureTime = minValue;
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("Feature ExposureTime not available.\n");
                            }

                            // Check if frame rate is available
                            if (device.IsFeatureAvailable("AcquisitionFrameRate"))
                            {
                                // Check if use max frame rate is true
                                if (UseMaxFrameRate)
                                {
                                    // Set frame rate variable to -1
                                    FrameRate = -1;
                                }
                                else
                                {
                                    // Check if frame rate has been manually set
                                    if (FrameRate == 0)
                                    {
                                        // Set frame rate variable to current value on camera
                                        device.GetFeatureValue("AcquisitionFrameRate", out doubleFeatureValue);
                                        FrameRate = doubleFeatureValue;
                                    }
                                    else
                                    {
                                        // Gets information about the frame rate
                                        device.GetFeatureInfo("AcquisitionFrameRate", feature);
                                        feature.GetValueMax(out maxValue);
                                        feature.GetValueMin(out minValue);

                                        if (FrameRate < maxValue && FrameRate > minValue)
                                        {
                                            // Set camera's frame rate to frame rate variable
                                            device.SetFeatureValue("AcquisitionFrameRate", FrameRate);
                                        }
                                        else if (FrameRate >= maxValue || FrameRate == -1)
                                        {
                                            // If the frame rate variable is greater than or equal to the maximum frame rate, set the frame rate to the maximum value
                                            device.SetFeatureValue("AcquisitionFrameRate", maxValue);
                                            FrameRate = maxValue;
                                        }
                                        else
                                        {
                                            // If the frame rate variable is less than or equal to the minumum frame rate, set the frame rate to the minumum value
                                            device.SetFeatureValue("AcquisitionFrameRate", minValue);
                                            FrameRate = minValue;
                                        }
                                    }
                                }
                            }
                            
                            // Check if black level is available
                            if (device.IsFeatureAvailable("BlackLevel"))
                            {
                                // Check if black level has been manually set
                                if (BlackLevel == 0)
                                {
                                    // Set black level parameter to the camera's current black level setting
                                    device.GetFeatureValue("BlackLevel", out doubleFeatureValue);
                                    BlackLevel = doubleFeatureValue;
                                }
                                else
                                {
                                    // Gets information about the black level
                                    device.GetFeatureInfo("BlackLevel", feature);
                                    feature.GetValueMax(out maxValue);
                                    feature.GetValueMin(out minValue);

                                    if (BlackLevel < maxValue && BlackLevel > minValue)
                                    {
                                        // Set camera's black level to black level variable
                                        device.SetFeatureValue("BlackLevel", BlackLevel);
                                    }
                                    else if (BlackLevel >= maxValue)
                                    {
                                        // If the black level variable is greater than or equal to the maximum black level, set the black level to the maximum value
                                        device.SetFeatureValue("BlackLevel", maxValue);
                                        BlackLevel = maxValue;
                                    }
                                    else
                                    {
                                        // If the black level variable is less than or equal to the minumum black level, set the black level to the minumum value
                                        device.SetFeatureValue("BlackLevel", minValue);
                                        BlackLevel = minValue;
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("Feature BlackLevel not available.\n");
                            }

                            // Check if gain is available
                            if (device.IsFeatureAvailable("Gain"))
                            {
                                // Check if gain has been manually set
                                if (Gain == 0)
                                {
                                    // Set gain parameter to the camera's current gain setting
                                    device.GetFeatureValue("Gain", out doubleFeatureValue);
                                    Gain = doubleFeatureValue;
                                }
                                else 
                                {
                                    // Gets information about the gain
                                    device.GetFeatureInfo("Gain", feature);
                                    feature.GetValueMax(out maxValue);
                                    feature.GetValueMin(out minValue);

                                    if (Gain < maxValue && Gain > minValue)
                                    {
                                        // Set camera's gain to gain variable
                                        device.SetFeatureValue("Gain", Gain);
                                    }
                                    else if (Gain >= maxValue)
                                    {
                                        // If the gain variable is greater than or equal to the maximum gain, set the gain to the maximum value
                                        device.SetFeatureValue("Gain", maxValue);
                                        Gain = maxValue;
                                    }
                                    else
                                    {
                                        // If the gain variable is less than or equal to the minumum gain, set the gain to the minumum value
                                        device.SetFeatureValue("Gain", minValue);
                                        Gain = minValue;
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("Feature Gain not available.\n");
                            }

                            try
                            {
                                // Start grabbing frames
                                transfer.Grab();

                                while (!cancellationToken.IsCancellationRequested)
                                {
                                    // Do nothing
                                }

                                // Stop camera acquisition
                                transfer.Freeze();
                                transfer.Wait(5000);
                            }
                            finally
                            {
                                // Destroy objects
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                            }
                        }
                    },
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                })
            .PublishReconnectable()
            .RefCount();
            }
        }
    }
}
