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
    [Description("Produces a sequence of images acquired from an Allied Vision camera using the Vimba SDK.")]
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
        bool exposureTimeChanged;
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
                exposureTimeChanged = true;
            }
        }
        
        // Create frame rate parameter
        bool frameRateChanged;
        private double frameRate;
        [Description("Desired frame rate (frames / s). This is superceded by the maximum framerate allowed by the exposure time.")]
        public double FrameRate
        {
            get
            {
                return frameRate;
            }
            set
            {
                frameRate = Math.Floor(value * 100) / 100;
                frameRateChanged = true;
            }
        }

        // Create black level parameter
        bool blackLevelChanged;
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
                blackLevelChanged = true;
            }
        }

        // Create gain parameter
        bool gainChanged;
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
                gainChanged = true;
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
                frameRateChanged = true;
            }
        }

        private bool resetDevice;
        [Description("Resets the camera just prior to acquisition.")]
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
        Size frameSize;
        IplDepth depth;
        IObserver<SaperaDataFrame> global_observer;
        double maxFrameRate;
        double doubleFeatureValue;
        int width;
        int height;
        int intFeatureValue;

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

        // Checks camera's parameters
        private void CheckCameraParameters(SapAcqDevice device, SapFeature feature)
        {
            Console.WriteLine("CheckParameters Exposure");
            if (exposureTimeChanged)
            {
                // User changed the exposure time parameter.
                // Set the exposure time feature on the camera.
                device.SetFeatureValue("ExposureTime", ExposureTime * 1000.0);

                exposureTimeChanged = false;

                // Adjust the framerate given this new exposure time
                frameRateChanged = true;
            }
            Console.WriteLine("CheckParameters FrameRate");
            if (frameRateChanged)
            {
                if (!UseMaxFrameRate)
                {
                    // Get the maximum framerate for the current exposure time
                    device.GetFeatureInfo("AcquisitionFrameRate", feature);
                    Console.WriteLine("AcquisitionFrameRate: ", feature.DataType);
                    feature.GetValueMax(out maxFrameRate);
                    if (maxFrameRate <= FrameRate)
                    {
                        // If the frame rate variable exceeds the maximum frame rate, set the frame rate to the maximum value
                        device.SetFeatureValue("AcquisitionFrameRate", maxFrameRate);
                        FrameRate = maxFrameRate;
                    }
                    else
                    {
                        // Set camera's frame rate to frame rate variable
                        device.SetFeatureValue("AcquisitionFrameRate", FrameRate);
                    }
                }

                frameRateChanged = false;
            }
            Console.WriteLine("CheckParameters BlackLevel");
            if (blackLevelChanged)
            {
                // User changed the black level parameter.
                // Set the black level feature on the camera.
                device.SetFeatureValue("BlackLevel", BlackLevel);

                blackLevelChanged = false;
            }
            Console.WriteLine("CheckParameters Gain");
            if (gainChanged)
            {
                // User changed the gain parameter.
                // Set the gain feature on the camera.
                device.SetFeatureValue("Gain", Gain);

                gainChanged = false;
            }
            Console.WriteLine("CheckParameters End");
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
                output = new IplImage(frameSize, depth, 1, image).Clone();
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
                            global_observer = observer;

                            // Get server count
                            int serverCount = SapManager.GetServerCount();

                            if (serverCount == 0)
                            {
                                Console.WriteLine("No device found.\n");
                                return;
                            }

                            if (Index >= 0 && Index < serverCount - 1)
                            {
                                // Finds the name of the server
                                serverName = SapManager.GetServerName(Index + 1);
                            }
                            else
                            {
                                Console.WriteLine("Invalid index selection.\n");
                                return;
                            }

                            // Finds server location
                            location = new SapLocation(serverName, 0);

                            // Finds device
                            device = new SapAcqDevice(location, false);

                            // Creates buffer
                            buffer = new SapBufferWithTrash(3, device, SapBuffer.MemoryType.ScatterGather);

                            // Initializes transfer between device and buffer
                            transfer = new SapAcqDeviceToBuf(device, buffer);

                            // Checks if device was created
                            if (!device.Create())
                            {
                                Console.WriteLine("Error during SapAcqDevice creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Initializes frame handler for end of frame events
                            transfer.Pairs[0].EventType = SapXferPair.XferEventType.EndOfFrame;
                            transfer.XferNotify += new SapXferNotifyHandler(Xfer_XferNotify);
                            transfer.XferNotifyContext = buffer;

                            // Checks if buffer was created
                            if (!buffer.Create())
                            {
                                Console.WriteLine("Error during SapBuffer creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Checks if transfer layer was created
                            if (!transfer.Create())
                            {
                                Console.WriteLine("Error during SapAcqDeviceToBuf creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Creates feature object
                            feature = new SapFeature(location);

                            if (!feature.Create())
                            {
                                Console.WriteLine("Error during SapFeature creation.\n");
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();
                                return;
                            }

                            // Resets device
                            if (resetDevice)
                            {
                                DestroyObjects(device, buffer, transfer, feature);
                                location.Dispose();

                                SapManager.ResetServer(serverName, true);

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
                                Console.WriteLine("SapBuffer created.\n");

                                if (!transfer.Create())
                                {
                                    Console.WriteLine("Error during SapAcqDeviceToBuf creation.\n");
                                    DestroyObjects(device, buffer, transfer, feature);
                                    location.Dispose();
                                    return;
                                }
                                Console.WriteLine("SapAcqDeviceToBuf created.\n");

                                Console.WriteLine("Creating SapFeature.\n");
                                feature = new SapFeature(location);
                                if (!feature.Create())
                                {
                                    Console.WriteLine("Error during SapFeature creation.\n");
                                    DestroyObjects(device, buffer, transfer, feature);
                                    location.Dispose();
                                    return;
                                }
                            }

                            if (device.IsFeatureAvailable("acquisitionFrameRateControlMode"))
                            {
                                // Checks whether to set frame rate to max
                                if (UseMaxFrameRate)
                                {
                                    device.SetFeatureValue("acquisitionFrameRateControlMode", "MaximumSpeed");
                                }
                                else
                                {
                                    device.SetFeatureValue("acquisitionFrameRateControlMode", "Programmable");
                                }
                            }

                            // Sets exposure time and frame rate to camera's current values
                            if (ExposureTime == 0 && FrameRate == 0)
                            {
                                if (device.IsFeatureAvailable("ExposureTime"))
                                {
                                    // Set exposure time variable to current value on the camera (convert from us to ms)
                                    device.GetFeatureValue("ExposureTime", out doubleFeatureValue);
                                    ExposureTime = doubleFeatureValue / 1000.0;
                                }

                                if (device.IsFeatureAvailable("AcquisitionFrameRate") && !UseMaxFrameRate)
                                {
                                    // Set frame rate variable to current value on camera
                                    device.GetFeatureValue("AcquisitionFrameRate", out doubleFeatureValue);
                                    FrameRate = doubleFeatureValue;
                                }
                            }
                            // Change camera's frame rate if frame rate has been manually set
                            else if (ExposureTime == 0 && FrameRate != 0)
                            {
                                if (device.IsFeatureAvailable("ExposureTime"))
                                {
                                    // Set exposure time variable to current value on the camera (convert from us to ms)
                                    device.GetFeatureValue("ExposureTime", out doubleFeatureValue);
                                    ExposureTime = doubleFeatureValue / 1000.0;
                                }

                                if (device.IsFeatureAvailable("AcquisitionFrameRate"))
                                {
                                    if (!UseMaxFrameRate)
                                    {
                                        device.GetFeatureInfo("AcquisitionFrameRate", feature);
                                        feature.GetValueMax(out maxFrameRate);
                                        if (maxFrameRate <= FrameRate)
                                        {
                                            // If the frame rate variable exceeds the maximum frame rate, set the frame rate to the maximum value
                                            device.SetFeatureValue("AcquisitionFrameRate", maxFrameRate);
                                            FrameRate = maxFrameRate;
                                        }
                                        else
                                        {
                                            // Set camera's frame rate to frame rate variable
                                            device.SetFeatureValue("AcquisitionFrameRate", FrameRate);
                                        }
                                    }
                                }
                            }
                            // Change camera's exposure time if exposure time has been manually set
                            else if (ExposureTime != 0 && FrameRate == 0)
                            {
                                if (device.IsFeatureAvailable("ExposureTime"))
                                {
                                    // Sets camera's exposure time to exposure time variable
                                    device.GetFeatureInfo("ExposureTime", feature);
                                    device.SetFeatureValue("ExposureTime", ExposureTime * 1000.0);
                                }

                                if (device.IsFeatureAvailable("AcquisitionFrameRate") && !UseMaxFrameRate)
                                {
                                    // Set frame rate variable to current value on camera
                                    device.GetFeatureInfo("AcquisitionFrameRate", feature);
                                    device.GetFeatureValue("AcquisitionFrameRate", out doubleFeatureValue);
                                    FrameRate = doubleFeatureValue;
                                }
                            }
                            // Changes camera's exposure time and frame rate to match the manually changed variables
                            else
                            {
                                if (device.IsFeatureAvailable("ExposureTime"))
                                {
                                    // Sets camera's exposure time to exposure time variable
                                    device.GetFeatureInfo("ExposureTime", feature);
                                    device.SetFeatureValue("ExposureTime", ExposureTime * 1000.0);
                                }

                                if (device.IsFeatureAvailable("AcquisitionFrameRate"))
                                {
                                    if (!UseMaxFrameRate)
                                    {
                                        device.GetFeatureInfo("AcquisitionFrameRate", feature);
                                        feature.GetValueMax(out maxFrameRate);
                                        if (maxFrameRate <= FrameRate)
                                        {
                                            // If the frame rate variable exceeds the maximum frame rate, set the frame rate to the maximum value
                                            device.SetFeatureValue("AcquisitionFrameRate", maxFrameRate);
                                            FrameRate = maxFrameRate;
                                        }
                                        else
                                        {
                                            // Set camera's frame rate to frame rate variable
                                            device.SetFeatureValue("AcquisitionFrameRate", FrameRate);
                                        }
                                    }
                                }
                            }

                            // Set black level parameter to the camera's current black level setting
                            if (BlackLevel == 0)
                            {
                                if (device.IsFeatureAvailable("BlackLevel"))
                                {
                                    device.GetFeatureInfo("BlackLevel", feature);
                                    device.GetFeatureValue("BlackLevel", out doubleFeatureValue);
                                    BlackLevel = doubleFeatureValue;
                                }                              
                            }
                            // Changes the camera's black level if black level has been set manually
                            else
                            {
                                if (device.IsFeatureAvailable("BlackLevel"))
                                {
                                    device.GetFeatureInfo("BlackLevel", feature);
                                    device.SetFeatureValue("BlackLevel", BlackLevel);
                                }
                            }

                            // Set gain parameter to the camera's current gain setting
                            if (Gain == 0)
                            {
                                if (device.IsFeatureAvailable("Gain"))
                                {
                                    device.GetFeatureInfo("Gain", feature);
                                    device.GetFeatureValue("Gain", out doubleFeatureValue);
                                    Gain = doubleFeatureValue;
                                }
                            }
                            // Changes the camera's gain if gain has been set manually
                            else if (Gain != 0)
                            {
                                if (device.IsFeatureAvailable("Gain"))
                                {
                                    device.GetFeatureInfo("Gain", feature);
                                    device.SetFeatureValue("Gain", Gain);
                                }
                            }

                            // Set frameSize & depth features
                            device.GetFeatureInfo("SensorWidth", feature);
                            device.GetFeatureValue("SensorWidth", out intFeatureValue);
                            width = intFeatureValue;
                            device.GetFeatureInfo("SensorHeight", feature);
                            device.GetFeatureValue("SensorHeight", out intFeatureValue);
                            height = intFeatureValue;
                            frameSize = new Size(width, height);
                            depth = IplDepth.U8;

                            try
                            {
                                // Start capturing frames
                                transfer.Grab();

                                while (!cancellationToken.IsCancellationRequested)
                                {
                                    // Check the parameters of the camera
                                    // CheckCameraParameters(device, feature);
                                }

                                // Stop camera acquisition
                                transfer.Freeze();
                                transfer.Wait(5000);
                            }
                            finally
                            {
                                // Shutdown everything
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
