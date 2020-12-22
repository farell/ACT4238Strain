using DataAcquisition;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SQLite;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ACT4238Strain
{
    public partial class Form1 : Form
    {
        //Here is the once-per-class call to initialize the log object
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        /// <summary>
        /// 设备列表
        /// </summary>
        private Dictionary<string, SerialPortDevice> deviceList;
        private string database = "DataSource = LongRui.db";
        /// <summary>
        /// 控制采集周期
        /// </summary>
        private System.Timers.Timer timer;

        private BackgroundWorker backgroundWorker1;
        private ConnectionMultiplexer redis;

        public Form1()
        {
            InitializeComponent();
            redis = ConnectionMultiplexer.Connect("localhost,abortConnect=false");
            backgroundWorker1 = new BackgroundWorker();
            backgroundWorker1.WorkerSupportsCancellation = true;
            backgroundWorker1.DoWork += backgroundWorker1_DoWork;
            deviceList = new Dictionary<string, SerialPortDevice>();
            timer = new System.Timers.Timer();
            timer.Elapsed += Timer_Elapsed;
            LoadDevices(12);
            buttonStopAcquisit.Enabled = false;
        }

        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (backgroundWorker1.IsBusy)
            {
                this.Invoke((EventHandler)(
                delegate
                {
                    //textBox1.AppendText("正在采集数据，无法开始新的采集周期" + "\r\n");
                }));
            }
            else
            {
                backgroundWorker1.RunWorkerAsync(numericUpDown1.Value);
            }
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SerialPortDevice aCT4238 = this.deviceList[textBoxId.Text];
            aCT4238.Acquisit(stamp);
            //SerialACT4238Config ssbc = new SerialACT4238Config(textBox2.Text, 9600, 15, "1", "8");
            //SerialACT4238 ssb = new SerialACT4238(ssbc);
            //ssb.Acquisit();
            MessageBox.Show(aCT4238.GetResultString());

        }

        private Dictionary<int, StrainChannel> LoadChannels(SerialACT4238Config config)
        {
            Dictionary<int, StrainChannel> strainChannels = new Dictionary<int, StrainChannel>();
            using (SQLiteConnection connection = new SQLiteConnection(config.Database))
            {
                connection.Open();
                string strainStatement = "select SensorId,ChannelNo,G,R0,K,T0,Constant,InitVal,Desc from StrainChannels where GroupNo ='" + config.DeviceId + "'";
                SQLiteCommand command = new SQLiteCommand(strainStatement, connection);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string sensorId = reader.GetString(0);
                        int channelNo = reader.GetInt32(1);
                        double g = reader.GetDouble(2);
                        double r0 = reader.GetDouble(3);
                        double k = reader.GetDouble(4);
                        double t0 = reader.GetDouble(5);
                        double constant = reader.GetDouble(6);
                        double initVal = reader.GetDouble(7);
                        string desc = reader.GetString(8);

                        int index = this.dataGridView1.Rows.Add();
                        this.dataGridView1.Rows[index].Cells[0].Value = desc;

                        StrainChannel channel = new StrainChannel(sensorId, g, r0, k, t0, constant, initVal, desc, index);
                        strainChannels.Add(channelNo, channel);
                    }
                    return strainChannels;
                }
            }
        }

        private void LoadDevices(int period)
        {
            string[] ports = SerialPort.GetPortNames();
            this.deviceList.Clear();
            using (SQLiteConnection connection = new SQLiteConnection(database))
            {
                connection.Open();
                string strainStatement = "select PortName,BaudRate,DeviceId,Timeout,Type,Desc from SensorInfo";
                SQLiteCommand command2 = new SQLiteCommand(strainStatement, connection);
                using (SQLiteDataReader reader = command2.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string portName = reader.GetString(0);
                        int baudrate = reader.GetInt32(1);
                        string deviceId = reader.GetString(2);
                        int timeout = reader.GetInt32(3);
                        string type = reader.GetString(4);
                        string description = reader.GetString(5);

                        string[] itemString = { description, portName, baudrate.ToString(), deviceId, type, timeout.ToString() };
                        ListViewItem item = new ListViewItem(itemString);

                        listView1.Items.Add(item);

                        SerialACT4238Config config = new SerialACT4238Config(portName, baudrate, timeout, deviceId, type);
                        Dictionary<int, StrainChannel> channels = LoadChannels(config);

                        SerialACT4238 device = null;

                        if (type == "ACT4238")
                        {
                            if (!ports.Contains(portName))
                            {
                                log.Error(portName + " does not exist!");
                            }
                            else
                            {
                                device = new SerialACT4238(config, channels, redis);
                                device.UpdateDataGridView += new EventHandler<UpdateDataGridViewCellsEventArgs>(UpdateGridViewCell);
                            }
                        }
                        else { }

                        if (device != null)
                        {
                            this.deviceList.Add(deviceId, device);
                        }
                    }
                }
            }
        }

        private void UpdateGridViewCell(object sender, UpdateDataGridViewCellsEventArgs e)
        {
            List<UpdateArgs> args = e.args;
            if (dataGridView1.InvokeRequired)
            {
                dataGridView1.BeginInvoke(new MethodInvoker(() => {
                    foreach (var item in args)
                    {
                        dataGridView1.Rows[item.index].Cells[1].Value = item.stamp;
                        dataGridView1.Rows[item.index].Cells[2].Value = item.digit;

                        dataGridView1.Rows[item.index].Cells[4].Value = item.temp;
                        dataGridView1.Rows[item.index].Cells[3].Value = item.strain;
                        dataGridView1.Rows[item.index].Cells[5].Value = item.state;
                        dataGridView1.CurrentCell = dataGridView1.Rows[item.index].Cells[0];
                    }
                }));
            }
            else
            {
                foreach (var item in args)
                {
                    dataGridView1.Rows[item.index].Cells[1].Value = item.stamp;
                    dataGridView1.Rows[item.index].Cells[2].Value = item.digit;

                    dataGridView1.Rows[item.index].Cells[4].Value = item.temp;
                    dataGridView1.Rows[item.index].Cells[3].Value = item.strain;
                    dataGridView1.Rows[item.index].Cells[5].Value = item.state;
                    dataGridView1.CurrentCell = dataGridView1.Rows[item.index].Cells[0];
                }
            }

        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listView1.SelectedIndices != null && listView1.SelectedIndices.Count > 0)
            {
                ListView.SelectedIndexCollection c = listView1.SelectedIndices;
                textBoxId.Text = listView1.Items[c[0]].SubItems[3].Text;
                textBoxPort.Text = listView1.Items[c[0]].SubItems[1].Text;
            }
        }

        private void buttonStartAcquisit_Click(object sender, EventArgs e)
        {
            backgroundWorker1.RunWorkerAsync(numericUpDown1.Value);
            StartAcquisit();
        }

        private void buttonStopAcquisit_Click(object sender, EventArgs e)
        {
            backgroundWorker1.CancelAsync();
            StopAcquisit();
        }

        private void StartAcquisit()
        {
            groupBox1.Enabled = false;
            buttonStart.Enabled = false;
            buttonStop.Enabled = true;
            buttonStartAcquisit.Enabled = false;
            buttonStopAcquisit.Enabled = true;
            numericUpDown1.Enabled = false;
            timer.Interval = (double)numericUpDown1.Value * 60 * 1000;
            timer.Start();
        }

        private void StopAcquisit()
        {
            groupBox1.Enabled = true;
            buttonStart.Enabled = true;
            buttonStop.Enabled = false;
            numericUpDown1.Enabled = true;
            buttonStartAcquisit.Enabled = true;
            buttonStopAcquisit.Enabled = false;
            timer.Stop();
            if (backgroundWorker1.IsBusy)
            {
                backgroundWorker1.CancelAsync();
            }
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (SerialPortDevice spd in deviceList.Values)
            {
                spd.Acquisit(stamp);

                bool isSuccess = spd.GetAcquisiState();

                string res = stamp + " " + spd.GetResultString() + "\r\n";


                if (isSuccess)
                {
                    //SaveToDatabase(spd.GetDeviceId(), "offset", spd.GetResult(), stamp);
                }

                this.Invoke((EventHandler)(
                    delegate
                    {
                        //this.textBox1.AppendText(res);
                    }));
                Thread.Sleep(1000);
                if (bgWorker.CancellationPending == true)
                {
                    e.Cancel = true;
                    return;
                }
            }

            //PostData();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 注意判断关闭事件reason来源于窗体按钮，否则用菜单退出时无法退出!
            if (e.CloseReason == CloseReason.UserClosing)
            {
                //取消"关闭窗口"事件
                e.Cancel = true; // 取消关闭窗体 

                //使关闭时窗口向右下角缩小的效果
                this.WindowState = FormWindowState.Minimized;
                this.notifyIcon1.Visible = true;
                this.Hide();
                return;
            }
        }

        

        private void RestoreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Visible = true;
            this.WindowState = FormWindowState.Normal;
            this.notifyIcon1.Visible = true;
            this.Show();
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定要退出？", "系统提示", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
            {
                this.notifyIcon1.Visible = false;
                this.Close();
                this.Dispose();
            }
        }

        private void SaveToDatabase(string sensorId, string type, double value, string stamp)
        {
            SQLiteConnection conn = null;
            SQLiteCommand cmd;

            string database = "Data Source = LongRui.db";

            try
            {
                conn = new SQLiteConnection(database);
                conn.Open();

                cmd = conn.CreateCommand();

                {
                    cmd.CommandText = "insert into data values('" + sensorId + "','" + stamp + "','" + type + "'," + value.ToString() + ")";
                }

                cmd.ExecuteNonQuery();

                conn.Close();
            }
            catch (Exception ex)
            {
                conn.Close();
            }
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (this.Visible)
            {
                this.WindowState = FormWindowState.Minimized;
                this.notifyIcon1.Visible = true;
                this.Hide();
            }
            else
            {
                this.Visible = true;
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            }
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            log.Fatal("Test Test");
        }
    }
}
