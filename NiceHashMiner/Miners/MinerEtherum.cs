﻿using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace NiceHashMiner.Miners {

    /// <summary>
    /// For now used only for daggerhashimoto
    /// </summary>
    public abstract class MinerEtherum : Miner {
        
        //ComputeDevice
        protected ComputeDevice DaggerHashimotoGenerateDevice;

        readonly protected string CurrentBlockString;
        readonly private DagGenerationType DagGenerationType;

        public MinerEtherum(DeviceType deviceType, string minerDeviceName, string blockString)
            : base(deviceType, minerDeviceName) {
            Path = Ethereum.EtherMinerPath;
            _isEthMinerExit = true;
            CurrentBlockString = blockString;
            DagGenerationType = ConfigManager.Instance.GeneralConfig.EthminerDagGenerationType;
        }

        protected override int GET_MAX_CooldownTimeInMilliseconds() {
            return 90 * 1000; // 1.5 minute max, whole waiting time 75seconds
        }

        protected override void InitSupportedMinerAlgorithms() {
            _supportedMinerAlgorithms = new AlgorithmType[] { AlgorithmType.DaggerHashimoto };
        }

        protected abstract string GetStartCommandStringPart(Algorithm miningAlgorithm, string url, string username);
        protected abstract string GetBenchmarkCommandStringPart(ComputeDevice benchmarkDevice, Algorithm algorithm);

        protected override string GetDevicesCommandString() {
            string deviceStringCommand = " ";

            List<string> ids = new List<string>();
            foreach (var cdev in CDevs) {
                ids.Add(cdev.ID.ToString());
            }
            deviceStringCommand += string.Join(" ", ids);
            // set dag load mode
            deviceStringCommand += String.Format(" --dag-load-mode {0} ", GetDagGenerationString(DagGenerationType));
            if (DagGenerationType == DagGenerationType.Single
                || DagGenerationType == DagGenerationType.SingleKeep) {
                // set dag generation device
                deviceStringCommand += DaggerHashimotoGenerateDevice.ID.ToString();
            }
            return deviceStringCommand;
        }

        public static string GetDagGenerationString(DagGenerationType type) {
            switch (type) {
                case DagGenerationType.Parallel:
                    return "parallel";
                case DagGenerationType.Sequential:
                    return "sequential";
                case DagGenerationType.Single:
                    return "single";
                case DagGenerationType.SingleKeep:
                    return "singlekeep";
            }
            return "singlekeep";
        }

        public override void Start(Algorithm miningAlgorithm, string url, string username) {
            if (ProcessHandle == null) {
                CurrentMiningAlgorithm = miningAlgorithm;
                if (miningAlgorithm == null && miningAlgorithm.NiceHashID != AlgorithmType.DaggerHashimoto) {
                    Helpers.ConsolePrint(MinerTAG(), "Algorithm is null or not DaggerHashimoto");
                    return;
                }

                LastCommandLine = GetStartCommandStringPart(miningAlgorithm, url, username) + GetDevicesCommandString();
                ProcessHandle = _Start();
            } else {
                Helpers.ConsolePrint(MinerTAG(), ProcessTag() + " Resuming ethminer..");
                StartMining();
            }
        }

        protected override string BenchmarkCreateCommandLine(ComputeDevice benchmarkDevice, Algorithm algorithm, int time) {
            string CommandLine = GetBenchmarkCommandStringPart(benchmarkDevice, algorithm) + GetDevicesCommandString();
            Ethereum.GetCurrentBlock(CurrentBlockString);
            CommandLine += " --benchmark " + Ethereum.CurrentBlockNum;

            return CommandLine;
        }

        public override void SetCDevs(string[] deviceUUIDs) {
            base.SetCDevs(deviceUUIDs);
            // now find the fastest for DAG generation
            double fastestSpeed = double.MinValue;
            foreach (var cdev in CDevs) {
                double compareSpeed = cdev.DeviceBenchmarkConfig.AlgorithmSettings[AlgorithmType.DaggerHashimoto].BenchmarkSpeed;
                if (fastestSpeed < compareSpeed) {
                    DaggerHashimotoGenerateDevice = cdev;
                    fastestSpeed = compareSpeed;
                }
            }
        }

        public override APIData GetSummary() {
            APIData ad = new APIData();

            FillAlgorithm("daggerhashimoto", ref ad);
            bool ismining;
            var getSpeedStatus = GetSpeed(out ismining, out ad.Speed);
            if (GetSpeedStatus.GOT == getSpeedStatus) {
                // fix MH/s
                ad.Speed *= 1000 * 1000;
                _currentMinerReadStatus = MinerAPIReadStatus.GOT_READ;
                // check if speed zero
                if (ad.Speed == 0) _currentMinerReadStatus = MinerAPIReadStatus.READ_SPEED_ZERO;
                return ad;
            } else if (GetSpeedStatus.NONE == getSpeedStatus) {
                ad.Speed = 0;
                _currentMinerReadStatus = MinerAPIReadStatus.NONE;
                return ad;
            }
            // else if (GetSpeedStatus.EXCEPTION == getSpeedStatus) {
            // we don't restart unles not responding for long time check cooldown logic in Miner
            //Helpers.ConsolePrint(MinerTAG(), "ethminer is not running.. restarting..");
            //IsRunning = false;
            _currentMinerReadStatus = MinerAPIReadStatus.NONE;
            return null;
        }

        protected override NiceHashProcess _Start() {
            SetEthminerAPI(APIPort);
            return base._Start();
        }

        protected override void _Stop(bool willswitch) {
            // prevent logging non runing miner
            if (!IsRunning) return;
            if (willswitch) {
                // daggerhashimoto - we only "pause" mining
                Helpers.ConsolePrint(MinerTAG(), ProcessTag() + " Pausing ethminer..");
                StopMining();
                return;
            }

            Helpers.ConsolePrint(MinerTAG(), ProcessTag() + " Shutting down miner");
            ChangeToNextAvaliablePort();
            if (!willswitch && ProcessHandle != null) {
                try {
                    ProcessHandle.Kill();
                } catch {
                } finally {
                    ProcessHandle = null;
                }
            }
        }

        public override string GetOptimizedMinerPath(AlgorithmType algorithmType, string devCodename, bool isOptimized) {
            return Ethereum.EtherMinerPath;
        }

        protected override bool UpdateBindPortCommand(int oldPort, int newPort) {
            // --api-port 
            const string MASK = "--api-port {0}";
            var oldApiBindStr = String.Format(MASK, oldPort);
            var newApiBindStr = String.Format(MASK, newPort);
            if (LastCommandLine.Contains(oldApiBindStr)) {
                LastCommandLine = LastCommandLine.Replace(oldApiBindStr, newApiBindStr);
                return true;
            }
            return false;
        }

        // benchmark stuff
        protected override bool BenchmarkParseLine(string outdata) {
            if (outdata.Contains("min/mean/max:")) {
                string[] splt = outdata.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                int index = Array.IndexOf(splt, "mean");
                double avg_spd = Convert.ToDouble(splt[index + 2]);
                Helpers.ConsolePrint("BENCHMARK", "Final Speed: " + avg_spd + "H/s");

                BenchmarkAlgorithm.BenchmarkSpeed = avg_spd;
                return true;
            }

            return false;
        }

        protected override void BenchmarkOutputErrorDataReceivedImpl(string outdata) {
            CheckOutdata(outdata);
        }

        #region ethminerAPI

        private enum GetSpeedStatus {
            NONE,
            GOT,
            EXCEPTION
        }

        /// <summary>
        /// Initialize ethminer API instance.
        /// </summary>
        /// <param name="port">ethminer's API port.</param>
        private void SetEthminerAPI(int port) {
            m_port = port;
            m_client = new UdpClient("127.0.0.1", port);
        }

        /// <summary>
        /// Call this to start ethminer. If ethminer is already running, nothing happens.
        /// </summary>
        private void StartMining() {
            SendUDP(2);
            IsRunning = true;
        }

        /// <summary>
        /// Call this to stop ethminer. If ethminer is already stopped, nothing happens.
        /// </summary>
        private void StopMining() {
            SendUDP(1);
            IsRunning = false;
        }

        /// <summary>
        /// Call this to get current ethminer speed. This method may block up to 2 seconds.
        /// </summary>
        /// <param name="ismining">Set to true if ethminer is not mining (has been stopped).</param>
        /// <param name="speed">Current ethminer speed in MH/s.</param>
        /// <returns>False if ethminer is unreachable (crashed or unresponsive and needs restarting).</returns>
        private GetSpeedStatus GetSpeed(out bool ismining, out double speed) {
            speed = 0;
            ismining = false;

            SendUDP(3);

            DateTime start = DateTime.Now;

            while ((DateTime.Now - start) < TimeSpan.FromMilliseconds(2000)) {
                if (m_client.Available > 0) {
                    // read
                    try {
                        IPEndPoint ipep = new IPEndPoint(IPAddress.Parse("127.0.0.1"), m_port);
                        byte[] data = m_client.Receive(ref ipep);
                        if (data.Length != 8) return GetSpeedStatus.NONE;
                        speed = BitConverter.ToDouble(data, 0);
                        if (speed >= 0) ismining = true;
                        else speed = 0;
                        return GetSpeedStatus.GOT;
                    } catch {
                        Helpers.ConsolePrint(MinerTAG(), ProcessTag() + " Could not read data from API bind port");
                        return GetSpeedStatus.EXCEPTION;
                    }
                } else
                    System.Threading.Thread.Sleep(2);
            }

            return GetSpeedStatus.NONE;
        }

        #region PRIVATE

        private int m_port;
        private UdpClient m_client;

        private void SendUDP(int code) {
            byte[] data = new byte[1];
            data[0] = (byte)code;
            m_client.Send(data, data.Length);
        }
        #endregion

        #endregion //ethminerAPI

    }
}
