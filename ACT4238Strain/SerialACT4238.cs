using MsgPack.Serialization;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DataAcquisition
{
    class SerialACT4238Config
    {
        public string Database = "Data Source = LongRui.db";
        public string PortName;
        public int Baudrate;
        public int Timeout;
        public string DeviceId;
        public string Type;

        public SerialACT4238Config(string portName,int baudrate,int timeout, string deviceId, string type)
        {
            this.PortName = portName;
            this.Baudrate = baudrate;
            this.Timeout = timeout;
            this.DeviceId = deviceId;
            this.Type = type;
        }
    }

    public class DataValue
    {
        public string SensorId { get; set; }
        public string TimeStamp { get; set; }
        public string ValueType { get; set; }
        public double Value { get; set; }
    }

    class SerialACT4238 : SerialPortDevice
    {
        //Here is the once-per-class call to initialize the log object
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private SerialACT4238Config config;
        private ManualResetEvent mre;
        private Dictionary<int, StrainChannel> strainChannels;
        public event EventHandler<UpdateDataGridViewCellsEventArgs> UpdateDataGridView;
        private double result;
        private string errMsg;
        private bool isSuccess;
        private string message;
        private const int NumberOfChannels = 8;
        private IDatabase db;
        private MessagePackSerializer serializer;
        private string Tag;
        public SerialACT4238(SerialACT4238Config config, Dictionary<int, StrainChannel> channels, ConnectionMultiplexer client) : base(config.PortName, config.Baudrate)
        {
            this.Tag = config.PortName + " : ";
            this.config = config;
            serializer = MessagePackSerializer.Get<DataValue>();
            db = client.GetDatabase();
            this.mre = new ManualResetEvent(false);
            strainChannels = channels;
        }

        public override void Acquisit(string stamp)
        {
            base.Acquisit(stamp);
            bool ret;

            this.isSuccess = false;
            try
            {
                this.Start();
            }
            catch (Exception ex)
            {
                log.Error(Tag+"打开串口错误", ex);
                errMsg = ex.Message;
                ret = false;
            }

            byte[] frame = GetAcquisitionFrame();
            this.SendFrame(frame);
            bool receivedEvent = this.mre.WaitOne(this.config.Timeout * 1000);
            if (receivedEvent)
            {
                if (isSuccess)
                {
                    ret = true;
                }
                else
                {
                    ret = false;
                }
            }
            else
            {
                log.Warn(Tag + " 接收数据超时");
                errMsg = "Timeout";
                ret = false;
            }
            mre.Reset();
            base.Stop();
        }

        public override bool GetAcquisiState()
        {
            return isSuccess;
        }

        private bool StrainFrameCheck(byte[] buffer,int length)
        {
            if (length != 45)
            {
                return false;
            }
            byte[] crc = ModbusUtility.CalculateCrc(buffer, 43);
            if (crc[0] == buffer[43] && crc[1] == buffer[44])
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool RtuFrameCheck(byte[] buffer, int length)
        {
            //if (buffer.Length != 13)
            //{
            //    return false;
            //}
            byte[] crc = ModbusUtility.CalculateCrc(buffer, length - 2);
            if (crc[0] == buffer[length - 2] && crc[1] == buffer[length - 1])
            {
                return true;
            }
            else
            {
                log.Warn(Tag+" Data frame check error");
                return false;
            }
        }

        protected virtual void OnUpdateDataGridView(UpdateDataGridViewCellsEventArgs e)
        {
            //EventHandler<UpdateDataGridViewCellsEventArgs> handler = UpdateDataGridView;
            //if (handler != null)
            //{
            //    handler(this, e);
            //}
            UpdateDataGridView?.Invoke(this, e);
        }

        public override string GetResultString()
        {
            if (isSuccess)
            {
                return GetObjectType() + " " + this.config.DeviceId + " : " + message;
            }
            else
            {
                return GetObjectType() + " " + this.config.DeviceId + " : " + errMsg;
            }

        }

        public override string GetObjectType()
        {
            return "SerialACT4238";
        }

        private byte[] GetAcquisitionFrame()
        {
            byte id = 1;//Byte.Parse(this.config.DeviceId);
            byte startAddress = 0;
            ushort numOfPoints = 16;
            byte[] frame = ModbusUtility.GetReadHoldingRegisterFrame(id, startAddress, numOfPoints);
            return frame;
        }

        public override void ProcessData(byte[] buffer, int length)
        {
            this.isSuccess = false;

            bool checkPassed = this.StrainFrameCheck(buffer,length);
            if (checkPassed == true)
            {
                int startIndex = 3;
                
                List<UpdateArgs> args = new List<UpdateArgs>();
                for (int i = 0; i < NumberOfChannels; i++)
                {
                    //频率
                    double digit = CalculateDigit(buffer,startIndex, i);

                    //温度
                    double temperature = CalculateTempreture(buffer, startIndex, i);
                    
                    //修正公式
                    double strain;

                    result = digit;

                    if (strainChannels.ContainsKey(i + 1))
                    {
                        StrainChannel sc = strainChannels[i + 1];
                        if (sc != null)
                        {
                            strain = sc.CalculateStrain(digit, temperature);

                            bool isSuccess = false;
                            string strainState = "";
                            if (digit < 0.001 || Math.Abs(temperature) < 0.001)
                            {
                                log.Warn(Tag+"Sensor Error: digit=" + digit.ToString() + "temperature=" + temperature.ToString());
                                isSuccess = false;
                                strainState  = "频率或者温度为零无法计算";
                            }
                            else
                            {
                                isSuccess = true;
                                strainState = "Success";
                                //SaveToDatabase(sc.SensorId, "009", strain,this.stamp);
                                //SaveToDatabase(sc.SensorId, "005", temperature, this.stamp);
                                string redisKeyTemp = sc.SensorId + "-005";
                                string redisKeyStrain = sc.SensorId + "-009";
                                DataValue dv = new DataValue();
                                dv.SensorId = sc.SensorId;
                                dv.TimeStamp = stamp;
                                dv.Value = temperature;
                                dv.ValueType = "005";

                                byte[] result = serializer.PackSingleObject(dv);
                                db.StringSet(redisKeyTemp, result);

                                dv.SensorId = sc.SensorId;
                                dv.TimeStamp = stamp;
                                dv.Value = strain;
                                dv.ValueType = "009";
                                result = serializer.PackSingleObject(dv);
                                db.StringSet(redisKeyStrain, result);
                            }

                            UpdateArgs arg = new UpdateArgs();
                            arg.index = sc.gridIndex;
                            arg.digit = digit;
                            arg.stamp = stamp;
                            arg.temp = temperature;
                            arg.strain = strain.ToString();
                            arg.state = strainState;

                            args.Add(arg);
                        }
                    }
                }
                UpdateDataGridViewCellsEventArgs eventArgs = new UpdateDataGridViewCellsEventArgs();
                eventArgs.args = args;
                OnUpdateDataGridView(eventArgs);

                this.isSuccess = true;
            }
            else
            {
                log.Warn(Tag+"broken frame");
                this.errMsg = "broken frame";
            }
            this.mre.Set();
        }
        private static double CalculateTempreture(byte[] buffer,int startIndex, int i)
        {
            //double temp = (buffer[startIndex + i * 5 + 3] * 256 + buffer[startIndex + i * 5 + 4]) / 10.0;
            byte sign = (byte)(buffer[startIndex + i * 5 + 3]&0x80);
            byte higher = (byte)(buffer[startIndex + i * 5 + 3] & 0x7F);
            byte lower = buffer[startIndex + i * 5 + 4];
            double temp = (higher * 256 + lower) / 10.0;
            if(sign == 0x80)
            {
                temp = -temp;
            }
            return  Math.Round(temp, 3);
        }

        private static double CalculateDigit(byte[] buffer, int startIndex, int i)
        {
            double strain = (buffer[startIndex + i * 5 + 0] * 256 * 256 + buffer[startIndex + i * 5 + 1] * 256 + buffer[startIndex + i * 5 + 2]) / 100.0;
            //频率转化为模数
            strain = strain * strain / 1000;
            return Math.Round(strain, 3);
        }
    }

    public class UpdateDataGridViewCellsEventArgs : EventArgs
    {
        public List<UpdateArgs> args;
    }

    public class UpdateArgs
    {
        public int index { get; set; }
        public string stamp { get; set; }
        public double digit { get; set; }
        public string strain { get; set; }
        public double temp { get; set; }
        public string state { get; set; }
    }

    class StrainChannel
    {
        public string SensorId;
        public double G;
        public double R0;
        public double K;
        public double T0;
        public double C;
        public double InitStrain;
        public double currentValue;
        public bool IsUpdated;
        public string description;
        public int gridIndex;

        public StrainChannel(string sensorId, double g, double r0, double k, double t0, double c, double initValue,string desc,int gridIndex)
        {
            this.SensorId = sensorId;
            this.gridIndex = gridIndex;
            this.G =  g;
            this.R0 = r0;
            this.K =  k;
            this.T0 = t0;
            this.C =  c;
            this.InitStrain = initValue;
            this.currentValue = 0;
            this.IsUpdated = false;
            this.description = desc;
        }

        public double CalculateStrain(double frequency,double temperature)
        {
            currentValue = this.G * (frequency - this.R0) + this.K * (temperature - this.T0) + this.C - this.InitStrain; 
            return Math.Round(currentValue, 3);
        }
    }
}
