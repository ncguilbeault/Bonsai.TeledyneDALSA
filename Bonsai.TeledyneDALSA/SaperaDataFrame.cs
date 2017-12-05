using OpenCV.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DALSA.SaperaLT.SapClassBasic;

namespace Bonsai.TeledyneDALSA
{
    public class SaperaDataFrame
    {
        public SaperaDataFrame(IplImage image, ulong deviceTimeStamp, uint counterStamp, ulong hostCounterStamp)
        {
            Image = image;
            DeviceTimeStamp = deviceTimeStamp;
            CounterStamp = counterStamp;
            HostCounterStamp = hostCounterStamp;
        }

        public IplImage Image { get; private set; }

        public ulong DeviceTimeStamp { get; private set; }
        
        public uint CounterStamp { get; private set; }

        public ulong HostCounterStamp { get; private set; }

        public override string ToString()
        {
            return string.Format("{{Image={0}}}", Image);
        }
    }
}
