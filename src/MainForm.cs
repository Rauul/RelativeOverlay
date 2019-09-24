﻿/*
FuelOverlay is visual debugger for rF2 Shared Memory Plugin.

MainForm implementation, contains main loop and render calls.

Author: The Iron Wolf (vleonavicius@hotmail.com)
Website: thecrewchief.org
*/
using FuelOverlay.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static FuelOverlay.rFactor2Constants;

namespace FuelOverlay
{
    public partial class MainForm : Form
    {
        double fuelCapacity = 0.00;
        double quantity = 0.00;
        double quantityLastLap = 0.00;
        double[] usedPerLap = new double[3] { 0, 0, 0 };
        double bestLapLastSession = 0;
        double lastLapStartET = 0.00;
        double lapQuantity = 0.00;
        double fuelNeededToFinish = 0.00;
        int timeQuantityTotal = 0;
        int timeQuantityMinutes = 0;
        int timeQuantitySeconds = 0;
        int fullTanksNeeded = 0;
        bool firstUpdate = true;

        // Connection fields
        private const int CONNECTION_RETRY_INTERVAL_MS = 1000;
        private const int DISCONNECTED_CHECK_INTERVAL_MS = 15000;
        private const float DEGREES_IN_RADIAN = 57.2957795f;
        private const int LIGHT_MODE_REFRESH_MS = 33;

        System.Windows.Forms.Timer connectTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer disconnectTimer = new System.Windows.Forms.Timer();
        bool connected = false;

        private class MappedBuffer<MappedBufferT>
        {
            const int NUM_MAX_RETRIEES = 10;
            readonly int RF2_BUFFER_VERSION_BLOCK_SIZE_BYTES = Marshal.SizeOf(typeof(rF2MappedBufferVersionBlock));
            readonly int RF2_BUFFER_VERSION_BLOCK_WITH_SIZE_SIZE_BYTES = Marshal.SizeOf(typeof(rF2MappedBufferVersionBlockWithSize));

            readonly int BUFFER_SIZE_BYTES;
            readonly string BUFFER_NAME;

            // Holds the entire byte array that can be marshalled to a MappedBufferT.  Partial updates
            // only read changed part of buffer, ignoring trailing uninteresting bytes.  However,
            // to marshal we still need to supply entire structure size.  So, on update new bytes are copied
            // (outside of the mutex).
            byte[] fullSizeBuffer = null;

            MemoryMappedFile memoryMappedFile = null;

            bool partial = false;
            bool skipUnchanged = false;
            public MappedBuffer(string buffName, bool partial, bool skipUnchanged)
            {
                this.BUFFER_SIZE_BYTES = Marshal.SizeOf(typeof(MappedBufferT));
                this.BUFFER_NAME = buffName;
                this.partial = partial;
                this.skipUnchanged = skipUnchanged;
            }

            public void Connect()
            {
                this.memoryMappedFile = MemoryMappedFile.OpenExisting(this.BUFFER_NAME);

                // NOTE: Make sure that BUFFER_SIZE matches the structure size in the plugin (debug mode prints that).
                this.fullSizeBuffer = new byte[this.BUFFER_SIZE_BYTES];
            }

            public void Disconnect()
            {
                if (this.memoryMappedFile != null)
                    this.memoryMappedFile.Dispose();

                this.memoryMappedFile = null;
                this.fullSizeBuffer = null;

                this.ClearStats();
            }

            // Read success statistics.
            int numReadRetriesPreCheck = 0;
            int numReadRetries = 0;
            int numReadRetriesOnCheck = 0;
            int numReadFailures = 0;
            int numStuckFrames = 0;
            int numReadsSucceeded = 0;
            int numSkippedNoChange = 0;
            uint stuckVersionBegin = 0;
            uint stuckVersionEnd = 0;
            uint lastSuccessVersionBegin = 0;
            uint lastSuccessVersionEnd = 0;
            int maxRetries = 0;

            public string GetStats()
            {
                return string.Format("R1: {0}    R2: {1}    R3: {2}    F: {3}    ST: {4}    MR: {5}    SK:{6}    S:{7}", this.numReadRetriesPreCheck, this.numReadRetries, this.numReadRetriesOnCheck, this.numReadFailures, this.numStuckFrames, this.maxRetries, this.numSkippedNoChange, this.numReadsSucceeded);
            }

            public void ClearStats()
            {
                this.numReadRetriesPreCheck = 0;
                this.numReadRetries = 0;
                this.numReadRetriesOnCheck = 0;
                this.numReadFailures = 0;
                this.numStuckFrames = 0;
                this.numReadsSucceeded = 0;
                this.numSkippedNoChange = 0;
                this.maxRetries = 0;
            }

            public void GetMappedDataUnsynchronized(ref MappedBufferT mappedData)
            {
                using (var sharedMemoryStreamView = this.memoryMappedFile.CreateViewStream())
                {
                    var sharedMemoryStream = new BinaryReader(sharedMemoryStreamView);
                    var sharedMemoryReadBuffer = sharedMemoryStream.ReadBytes(this.BUFFER_SIZE_BYTES);

                    var handleBuffer = GCHandle.Alloc(sharedMemoryReadBuffer, GCHandleType.Pinned);
                    mappedData = (MappedBufferT)Marshal.PtrToStructure(handleBuffer.AddrOfPinnedObject(), typeof(MappedBufferT));
                    handleBuffer.Free();
                }
            }

            private void GetHeaderBlock<HeaderBlockT>(BinaryReader sharedMemoryStream, int headerBlockBytes, ref HeaderBlockT headerBlock)
            {
                sharedMemoryStream.BaseStream.Position = 0;
                var sharedMemoryReadBufferHeader = sharedMemoryStream.ReadBytes(headerBlockBytes);

                var handleBufferHeader = GCHandle.Alloc(sharedMemoryReadBufferHeader, GCHandleType.Pinned);
                headerBlock = (HeaderBlockT)Marshal.PtrToStructure(handleBufferHeader.AddrOfPinnedObject(), typeof(HeaderBlockT));
                handleBufferHeader.Free();
            }

            public void GetMappedData(ref MappedBufferT mappedData)
            {
                // This method tries to ensure we read consistent buffer view in three steps.
                // 1. Pre-Check:
                //       - read version header and retry reading this buffer if begin/end versions don't match.  This reduces a chance of 
                //         reading torn frame during full buffer read.  This saves CPU time.
                //       - return if version matches last failed read version (stuck frame).
                //       - return if version matches previously successfully read buffer.  This saves CPU time by avoiding the full read of most likely identical data.
                //
                // 2. Main Read: reads the main buffer + version block.  If versions don't match, retry.
                //
                // 3. Post-Check: read version header again and retry reading this buffer if begin/end versions don't match.  This covers corner case
                //                where buffer is being written to during the Main Read.
                //
                // While retrying, this method tries to avoid running CPU at 100%.
                //
                // There are multiple alternatives on what to do here:
                // * keep retrying - drawback is CPU being kept busy, but absolute minimum latency.
                // * Thread.Sleep(0)/Yield - drawback is CPU being kept busy, but almost minimum latency.  Compared to first option, gives other threads a chance to execute.
                // * Thread.Sleep(N) - relaxed approach, less CPU saturation but adds a bit of latency.
                // there are other options too.  Bearing in mind that minimum sleep on windows is ~16ms, which is around 66FPS, I doubt delay added matters much for Crew Chief at least.
                using (var sharedMemoryStreamView = this.memoryMappedFile.CreateViewStream())
                {
                    uint currVersionBegin = 0;
                    uint currVersionEnd = 0;

                    var retry = 0;
                    var sharedMemoryStream = new BinaryReader(sharedMemoryStreamView);
                    byte[] sharedMemoryReadBuffer = null;
                    var versionHeaderWithSize = new rF2MappedBufferVersionBlockWithSize();
                    var versionHeader = new rF2MappedBufferVersionBlock();

                    for (retry = 0; retry < MappedBuffer<MappedBufferT>.NUM_MAX_RETRIEES; ++retry)
                    {
                        var bufferSizeBytes = this.BUFFER_SIZE_BYTES;
                        // Read current buffer versions.
                        if (this.partial)
                        {
                            this.GetHeaderBlock<rF2MappedBufferVersionBlockWithSize>(sharedMemoryStream, this.RF2_BUFFER_VERSION_BLOCK_WITH_SIZE_SIZE_BYTES, ref versionHeaderWithSize);
                            currVersionBegin = versionHeaderWithSize.mVersionUpdateBegin;
                            currVersionEnd = versionHeaderWithSize.mVersionUpdateEnd;

                            bufferSizeBytes = versionHeaderWithSize.mBytesUpdatedHint != 0 ? versionHeaderWithSize.mBytesUpdatedHint : bufferSizeBytes;
                        }
                        else
                        {
                            this.GetHeaderBlock<rF2MappedBufferVersionBlock>(sharedMemoryStream, this.RF2_BUFFER_VERSION_BLOCK_SIZE_BYTES, ref versionHeader);
                            currVersionBegin = versionHeader.mVersionUpdateBegin;
                            currVersionEnd = versionHeader.mVersionUpdateEnd;
                        }

                        // If this is stale "out of sync" situation, that is, we're stuck in, no point in retrying here.
                        // Could be a bug in a game, plugin or a game crash.
                        if (currVersionBegin == this.stuckVersionBegin
                          && currVersionEnd == this.stuckVersionEnd)
                        {
                            ++this.numStuckFrames;
                            return;  // Failed.
                        }

                        // If version is the same as previously successfully read, do nothing.
                        if (this.skipUnchanged
                          && currVersionBegin == this.lastSuccessVersionBegin
                          && currVersionEnd == this.lastSuccessVersionEnd)
                        {
                            ++this.numSkippedNoChange;
                            return;
                        }

                        // Buffer version pre-check.  Verify if Begin/End versions match.
                        if (currVersionBegin != currVersionEnd)
                        {
                            Thread.Sleep(1);
                            ++numReadRetriesPreCheck;
                            continue;
                        }

                        // Read the mapped data.
                        sharedMemoryStream.BaseStream.Position = 0;
                        sharedMemoryReadBuffer = sharedMemoryStream.ReadBytes(bufferSizeBytes);

                        // Marshal version block.
                        var handleVersionBlock = GCHandle.Alloc(sharedMemoryReadBuffer, GCHandleType.Pinned);
                        versionHeader = (rF2MappedBufferVersionBlock)Marshal.PtrToStructure(handleVersionBlock.AddrOfPinnedObject(), typeof(rF2MappedBufferVersionBlock));
                        handleVersionBlock.Free();

                        currVersionBegin = versionHeader.mVersionUpdateBegin;
                        currVersionEnd = versionHeader.mVersionUpdateEnd;

                        // Verify if Begin/End versions match:
                        if (versionHeader.mVersionUpdateBegin != versionHeader.mVersionUpdateEnd)
                        {
                            Thread.Sleep(1);
                            ++numReadRetries;
                            continue;
                        }

                        // Read the version header one last time.  This is for the case, that might not be even possible in reality,
                        // but it is possible in my head.  Since it is cheap, no harm reading again really, aside from retry that
                        // sometimes will be required if buffer is updated between checks.
                        //
                        // Anyway, the case is
                        // * Reader thread reads updateBegin version and continues to read buffer. 
                        // * Simultaneously, Writer thread begins overwriting the buffer.
                        // * If Reader thread reads updateEnd before Writer thread finishes, it will look 
                        //   like updateBegin == updateEnd.But we actually just read a partially overwritten buffer.
                        //
                        // Hence, this second check is needed here.  Even if writer thread still hasn't finished writing,
                        // we still will be able to detect this case because now updateBegin version changed, so we
                        // know Writer is updating the buffer.

                        this.GetHeaderBlock<rF2MappedBufferVersionBlock>(sharedMemoryStream, this.RF2_BUFFER_VERSION_BLOCK_SIZE_BYTES, ref versionHeader);

                        if (currVersionBegin != versionHeader.mVersionUpdateBegin
                          || currVersionEnd != versionHeader.mVersionUpdateEnd)
                        {
                            Thread.Sleep(1);
                            ++this.numReadRetriesOnCheck;
                            continue;
                        }

                        // Marshal rF2 State buffer
                        this.MarshalDataBuffer(this.partial, sharedMemoryReadBuffer, ref mappedData);

                        // Success.
                        this.maxRetries = Math.Max(this.maxRetries, retry);
                        ++this.numReadsSucceeded;
                        this.stuckVersionBegin = this.stuckVersionEnd = 0;

                        // Save succeessfully read version to avoid re-reading.
                        this.lastSuccessVersionBegin = currVersionBegin;
                        this.lastSuccessVersionEnd = currVersionEnd;

                        return;
                    }

                    // Failure.  Save the frame version.
                    this.stuckVersionBegin = currVersionBegin;
                    this.stuckVersionEnd = currVersionEnd;

                    this.maxRetries = Math.Max(this.maxRetries, retry);
                    ++this.numReadFailures;
                }
            }

            private void MarshalDataBuffer(bool partial, byte[] sharedMemoryReadBuffer, ref MappedBufferT mappedData)
            {
                if (partial)
                {
                    // For marshalling to succeed we need to copy partial buffer into full size buffer.  While it is a bit of a waste, it still gives us gain
                    // of shorter time window for version collisions while reading game data.
                    Array.Copy(sharedMemoryReadBuffer, this.fullSizeBuffer, sharedMemoryReadBuffer.Length);
                    var handlePartialBuffer = GCHandle.Alloc(this.fullSizeBuffer, GCHandleType.Pinned);
                    mappedData = (MappedBufferT)Marshal.PtrToStructure(handlePartialBuffer.AddrOfPinnedObject(), typeof(MappedBufferT));
                    handlePartialBuffer.Free();
                }
                else
                {
                    var handleBuffer = GCHandle.Alloc(sharedMemoryReadBuffer, GCHandleType.Pinned);
                    mappedData = (MappedBufferT)Marshal.PtrToStructure(handleBuffer.AddrOfPinnedObject(), typeof(MappedBufferT));
                    handleBuffer.Free();
                }
            }
        }

        MappedBuffer<rF2Telemetry> telemetryBuffer = new MappedBuffer<rF2Telemetry>(rFactor2Constants.MM_TELEMETRY_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
        MappedBuffer<rF2Scoring> scoringBuffer = new MappedBuffer<rF2Scoring>(rFactor2Constants.MM_SCORING_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
        MappedBuffer<rF2Rules> rulesBuffer = new MappedBuffer<rF2Rules>(rFactor2Constants.MM_RULES_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
        MappedBuffer<rF2Extended> extendedBuffer = new MappedBuffer<rF2Extended>(rFactor2Constants.MM_EXTENDED_FILE_NAME, false /*partial*/, true /*skipUnchanged*/);

        // Marshalled views:
        rF2Telemetry telemetry;
        rF2Scoring scoring;
        rF2Rules rules;
        rF2Extended extended;

        // Track rF2 transitions.
        TransitionTracker tracker = new TransitionTracker();

        // Config
        IniFile config = new IniFile();
        float scale = 2.0f;
        float xOffset = 0.0f;
        float yOffset = 0.0f;
        int focusVehicle = 0;
        bool rotateAroundVehicle = true;
        bool logTiming = false;
        bool logLightMode = false;
        public static bool useStockCarRulesPlugin = false;

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMessage
        {
            public IntPtr Handle;
            public uint Message;
            public IntPtr WParameter;
            public IntPtr LParameter;
            public uint Time;
            public Point Location;
        }

        [DllImport("user32.dll")]
        public static extern int PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        public MainForm()
        {
            this.InitializeComponent();

            this.DoubleBuffered = true;
            this.StartPosition = FormStartPosition.Manual;
            //this.Location = new Point(Screen.PrimaryScreen.Bounds.Width / 2 - 1330, Screen.PrimaryScreen.Bounds.Height / 2 + 385);

            this.LoadConfig();
            this.connectTimer.Interval = CONNECTION_RETRY_INTERVAL_MS;
            this.connectTimer.Tick += ConnectTimer_Tick;
            this.disconnectTimer.Interval = DISCONNECTED_CHECK_INTERVAL_MS;
            this.disconnectTimer.Tick += DisconnectTimer_Tick;
            this.connectTimer.Start();
            this.disconnectTimer.Start();

            this.TopMost = true;

            Application.Idle += HandleApplicationIdle;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();

            if (disposing)
                Disconnect();

            base.Dispose(disposing);
        }

        // Amazing loop implementation by Josh Petrie from:
        // http://gamedev.stackexchange.com/questions/67651/what-is-the-standard-c-windows-forms-game-loop
        bool IsApplicationIdle()
        {
            NativeMessage result;
            return PeekMessage(out result, IntPtr.Zero, (uint)0, (uint)0, (uint)0) == 0;
        }

        void HandleApplicationIdle(object sender, EventArgs e)
        {
            while (this.IsApplicationIdle())
            {
                try
                {
                    this.MainUpdate();

                    if (base.WindowState == FormWindowState.Minimized)
                    {
                        // being lazy lazy lazy.
                        this.tracker.TrackTimings(ref this.scoring, ref this.telemetry, ref this.rules, ref this.extended, null, this.logTiming);
                    }
                    else
                    {
                        this.MainRender();
                    }

                    Thread.Sleep(LIGHT_MODE_REFRESH_MS);
                }
                catch (Exception)
                {
                    this.Disconnect();
                }
            }
        }

        long delayAccMicroseconds = 0;
        long numDelayUpdates = 0;
        float avgDelayMicroseconds = 0.0f;
        void MainUpdate()
        {
            if (!this.connected)
                return;

            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();

                extendedBuffer.GetMappedData(ref extended);
                scoringBuffer.GetMappedData(ref scoring);
                telemetryBuffer.GetMappedData(ref telemetry);
                rulesBuffer.GetMappedData(ref rules);

                watch.Stop();
                var microseconds = watch.ElapsedTicks * 1000000 / System.Diagnostics.Stopwatch.Frequency;
                this.delayAccMicroseconds += microseconds;
                ++this.numDelayUpdates;

                if (this.numDelayUpdates == 0)
                {
                    this.numDelayUpdates = 1;
                    this.delayAccMicroseconds = microseconds;
                }

                this.avgDelayMicroseconds = (float)this.delayAccMicroseconds / this.numDelayUpdates;
            }
            catch (Exception)
            {
                this.Disconnect();
            }
        }

        int getPlayerSlot()
        {
            for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; i++)
            {
                if (scoring.mVehicles[i].mIsPlayer == 1)
                    return i;
            }
            return -1;
        }

        bool NewLapStarted()
        {
            if (telemetry.mVehicles[getPlayerSlot()].mLapStartET != lastLapStartET)
                return true;

            return false;
        }






        void MainRender()
        {
            if (scoring.mScoringInfo.mInRealtime == 0)
            {
                firstUpdate = true;
                return;
            }
            // Telemetry
            int playerSlot = getPlayerSlot();

            var info = telemetry.mVehicles[playerSlot];

            quantity = info.mFuel;
            fuelCapacity = info.mFuelCapacity;

            if (firstUpdate)
            {
                quantityLastLap = quantity;
                firstUpdate = false;
            }

            if (NewLapStarted())
            {
                if (quantityLastLap - quantity > info.mFuelCapacity * 0.01)
                {
                    for (int i = 2; i > 0; i--)
                    {
                        usedPerLap[i] = usedPerLap[i - 1];
                    }
                    usedPerLap[0] = quantityLastLap - quantity;
                }

                quantityLastLap = quantity;
                lastLapStartET = info.mLapStartET;
            }

            int laps = 0;
            double total = 0;
            for (int i = 0; i < 3; i++)
            {
                if (usedPerLap[i] > 0)
                {
                    total += usedPerLap[i];
                    laps++;
                }
                else
                    break;
            }

            // Laps
            if (laps > 0)
            {
                double avgFuelConsumption = total / laps;
                lapQuantity = quantity / avgFuelConsumption;
            }

            // Scoring
            if (NewLapStarted())
            {
                if ((scoring.mVehicles[playerSlot].mBestLapTime < bestLapLastSession && scoring.mVehicles[playerSlot].mBestLapTime > 0) || bestLapLastSession < 1)
                    bestLapLastSession = scoring.mVehicles[playerSlot].mBestLapTime;
            }

            // Calculate time
            if (laps > 0 && (scoring.mVehicles[playerSlot].mBestLapTime > 0 || bestLapLastSession > 0))
            {
                double avgFuelConsumption = total / laps;

                if (scoring.mVehicles[playerSlot].mBestLapTime > 0)
                    timeQuantityTotal = (int)(quantity / (avgFuelConsumption / scoring.mVehicles[playerSlot].mBestLapTime));
                else
                    timeQuantityTotal = (int)(quantity / (avgFuelConsumption / bestLapLastSession));
            }

            timeQuantityMinutes = timeQuantityTotal / 60;
            timeQuantitySeconds = timeQuantityTotal % 60;

            //Calculate fuel needed to finish
            if (laps > 0 && (scoring.mVehicles[playerSlot].mBestLapTime > 0 || bestLapLastSession > 0))
            {
                double avgFuelConsumption = total / laps;

                if (scoring.mScoringInfo.mMaxLaps > 999999)
                    if (scoring.mVehicles[playerSlot].mBestLapTime > 0)
                        fuelNeededToFinish = (avgFuelConsumption / scoring.mVehicles[playerSlot].mBestLapTime * (scoring.mScoringInfo.mEndET - scoring.mScoringInfo.mCurrentET)) - quantity + avgFuelConsumption;
                    else
                        fuelNeededToFinish = (avgFuelConsumption / bestLapLastSession * (scoring.mScoringInfo.mEndET - scoring.mScoringInfo.mCurrentET)) - quantity + avgFuelConsumption;
                else
                    fuelNeededToFinish = (scoring.mScoringInfo.mMaxLaps - (scoring.mVehicles[playerSlot].mTotalLaps + (scoring.mVehicles[playerSlot].mLapDist / scoring.mScoringInfo.mLapDist))) * avgFuelConsumption - quantity;

            }

            fullTanksNeeded = (int)(fuelNeededToFinish / fuelCapacity);
            fuelNeededToFinish = fuelNeededToFinish % fuelCapacity;


            // Show the results
            fuelLabel.Text = quantity.ToString("0.00");
            lastLapLabel.Text = usedPerLap[0].ToString("0.00");
            lapsLabel.Text = lapQuantity.ToString("0.00");
            timeLabel.Text = timeQuantityMinutes.ToString() + ":" + timeQuantitySeconds.ToString("00");
            if (fullTanksNeeded <= 0)
                refuelLabel.Text = "" + Math.Ceiling(fuelNeededToFinish);
            else
                refuelLabel.Text = "(" + fullTanksNeeded + ") " + Math.Ceiling(fuelNeededToFinish);
        }






        int framesAvg = 20;
        int frame = 0;
        int fps = 0;
        Stopwatch fpsStopWatch = new Stopwatch();
        private void UpdateFPS()
        {
            if (this.frame > this.framesAvg)
            {
                this.fpsStopWatch.Stop();
                var tsSinceLastRender = this.fpsStopWatch.Elapsed;
                this.fps = tsSinceLastRender.Milliseconds > 0 ? (1000 * this.framesAvg) / tsSinceLastRender.Milliseconds : 0;
                this.fpsStopWatch.Restart();
                this.frame = 0;
            }
            else
                ++this.frame;
        }

        private static string GetStringFromBytes(byte[] bytes)
        {
            if (bytes == null)
                return "";

            var nullIdx = Array.IndexOf(bytes, (byte)0);

            return nullIdx >= 0
              ? Encoding.Default.GetString(bytes, 0, nullIdx)
              : Encoding.Default.GetString(bytes);
        }

        // Corrdinate conversion:
        // rF2 +x = screen +x
        // rF2 +z = screen -z
        // rF2 +yaw = screen -yaw
        // If I don't flip z, the projection will look from below.
        void View_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;

            this.tracker.TrackTimings(ref this.scoring, ref this.telemetry, ref this.rules, ref this.extended, g, this.logTiming);

            this.UpdateFPS();

            if (!this.connected)
            {
                var brush = new SolidBrush(System.Drawing.Color.Black);
                //g.DrawString("Not connected", SystemFonts.DefaultFont, brush, 3.0f, 3.0f);

                if (this.logLightMode)
                    return;
            }
            else
            {
                var brush = new SolidBrush(System.Drawing.Color.Green);

                float yStep = SystemFonts.DefaultFont.Height;
                var gameStateText = new StringBuilder();
                gameStateText.Append(
                  $"Plugin Version:    Expected: 3.0.1.0 64bit   Actual: {MainForm.GetStringFromBytes(this.extended.mVersion)} {(this.extended.is64bit == 1 ? "64bit" : "32bit")}    {(this.extended.mHostedPluginVars.StockCarRules_IsHosted == 1 ? "SCR Plugin Hosted" : "")}    FPS: {this.fps}");

                // Draw header
                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY);

                gameStateText.Clear();

                // Build map of mID -> telemetry.mVehicles[i]. 
                // They are typically matching values, however, we need to handle online cases and dropped vehicles (mID can be reused).
                var idsToTelIndices = new Dictionary<long, int>();
                for (int i = 0; i < this.telemetry.mNumVehicles; ++i)
                {
                    if (!idsToTelIndices.ContainsKey(this.telemetry.mVehicles[i].mID))
                        idsToTelIndices.Add(this.telemetry.mVehicles[i].mID, i);
                }

                var playerVehScoring = GetPlayerScoring(ref this.scoring);

                var scoringPlrId = playerVehScoring.mID;
                var playerVeh = new rF2VehicleTelemetry();
                int resolvedPlayerIdx = -1;  // We're fine here with unitialized vehicle telemetry..
                if (idsToTelIndices.ContainsKey(scoringPlrId))
                {
                    resolvedPlayerIdx = idsToTelIndices[scoringPlrId];
                    playerVeh = this.telemetry.mVehicles[resolvedPlayerIdx];
                }

                // Figure out prev session end player mID
                var playerSessionEndInfo = new rF2VehScoringCapture();
                for (int i = 0; i < this.extended.mSessionTransitionCapture.mNumScoringVehicles; ++i)
                {
                    var veh = this.extended.mSessionTransitionCapture.mScoringVehicles[i];
                    if (veh.mIsPlayer == 1)
                        playerSessionEndInfo = veh;
                }

                gameStateText.Append(
                  "mElapsedTime:\n"
                  + "mCurrentET:\n"
                  + "mElapsedTime-mCurrentET:\n"
                  + "mDetlaTime:\n"
                  + "mInvulnerable:\n"
                  + "mVehicleName:\n"
                  + "mTrackName:\n"
                  + "mLapStartET:\n"
                  + "mLapDist:\n"
                  + "mEndET:\n"
                  + "mPlayerName:\n"
                  + "mPlrFileName:\n\n"
                  + "Session Started:\n"
                  + "Sess. End Session:\n"
                  + "Sess. End Phase:\n"
                  + "Sess. End Place:\n"
                  + "Sess. End Finish:\n"
                  + "Display msg capture:\n"
                  );

                // Col 1 labels
                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY += yStep);

                gameStateText.Clear();

                gameStateText.Append(
                        $"{playerVeh.mElapsedTime:N3}\n"
                        + $"{this.scoring.mScoringInfo.mCurrentET:N3}\n"
                        + $"{(playerVeh.mElapsedTime - this.scoring.mScoringInfo.mCurrentET):N3}\n"
                        + $"{playerVeh.mDeltaTime:N3}\n"
                        + (this.extended.mPhysics.mInvulnerable == 0 ? "off" : "on") + "\n"
                        + $"{MainForm.GetStringFromBytes(playerVeh.mVehicleName)}\n"
                        + $"{MainForm.GetStringFromBytes(playerVeh.mTrackName)}\n"
                        + $"{playerVeh.mLapStartET:N3}\n"
                        + $"{this.scoring.mScoringInfo.mLapDist:N3}\n"
                        + (this.scoring.mScoringInfo.mEndET < 0.0 ? "Unknown" : this.scoring.mScoringInfo.mEndET.ToString("N3")) + "\n"
                        + $"{MainForm.GetStringFromBytes(this.scoring.mScoringInfo.mPlayerName)}\n"
                        + $"{MainForm.GetStringFromBytes(this.scoring.mScoringInfo.mPlrFileName)}\n\n"
                        + $"{this.extended.mSessionStarted != 0}\n"
                        + $"{TransitionTracker.GetSessionString(this.extended.mSessionTransitionCapture.mSession)}\n"
                        + $"{(rFactor2Constants.rF2GamePhase)this.extended.mSessionTransitionCapture.mGamePhase}\n"
                        + $"{playerSessionEndInfo.mPlace}\n"
                        + $"{(rFactor2Constants.rF2FinishStatus)playerSessionEndInfo.mFinishStatus}\n"
                        + $"{MainForm.GetStringFromBytes(this.extended.mDisplayedMessageUpdateCapture)}\n"
                        );

                // Col1 values
                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 145, currY);

                // Print buffer stats.
                gameStateText.Clear();
                gameStateText.Append(
                  "Telemetry:\n"
                  + "Scoring:\n"
                  + "Rules:\n"
                  + "Extended:\n"
                  + "Avg read:");

                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Black, 1500, 570);

                gameStateText.Clear();
                gameStateText.Append(
                  this.telemetryBuffer.GetStats() + '\n'
                  + this.scoringBuffer.GetStats() + '\n'
                  + this.rulesBuffer.GetStats() + '\n'
                  + this.extendedBuffer.GetStats() + '\n'
                  + this.avgDelayMicroseconds.ToString("0.000") + " microseconds");

                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Black, 1560, 570);

                if (this.scoring.mScoringInfo.mNumVehicles == 0
                  || resolvedPlayerIdx == -1)  // We need telemetry for stats below.
                    return;

                gameStateText.Clear();

                gameStateText.Append(
                  "mTimeIntoLap:\n"
                  + "mEstimatedLapTime:\n"
                  + "mTimeBehindNext:\n"
                  + "mTimeBehindLeader:\n"
                  + "mPitGroup:\n"
                  + "mLapDist(Plr):\n"
                  + "mLapDist(Est):\n"
                  + "yaw:\n"
                  + "pitch:\n"
                  + "roll:\n"
                  + "speed:\n");

                // Col 2 labels
                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX += 275, currY);
                gameStateText.Clear();


                // Calculate derivatives:
                var yaw = Math.Atan2(playerVeh.mOri[RowZ].x, playerVeh.mOri[RowZ].z);

                var pitch = Math.Atan2(-playerVeh.mOri[RowY].z,
                  Math.Sqrt(playerVeh.mOri[RowX].z * playerVeh.mOri[RowX].z + playerVeh.mOri[RowZ].z * playerVeh.mOri[RowZ].z));

                var roll = Math.Atan2(playerVeh.mOri[RowY].x,
                  Math.Sqrt(playerVeh.mOri[RowX].x * playerVeh.mOri[RowX].x + playerVeh.mOri[RowZ].x * playerVeh.mOri[RowZ].x));

                var speed = Math.Sqrt((playerVeh.mLocalVel.x * playerVeh.mLocalVel.x)
                  + (playerVeh.mLocalVel.y * playerVeh.mLocalVel.y)
                  + (playerVeh.mLocalVel.z * playerVeh.mLocalVel.z));

                // Estimate lapdist
                // See how much ahead telemetry is ahead of scoring update
                var delta = playerVeh.mElapsedTime - scoring.mScoringInfo.mCurrentET;
                var lapDistEstimated = playerVehScoring.mLapDist;
                if (delta > 0.0)
                {
                    var localZAccelEstimated = playerVehScoring.mLocalAccel.z * delta;
                    var localZVelEstimated = playerVehScoring.mLocalVel.z + localZAccelEstimated;

                    lapDistEstimated = playerVehScoring.mLapDist - localZVelEstimated * delta;
                }

                gameStateText.Append(
                  $"{playerVehScoring.mTimeIntoLap:N3}\n"
                  + $"{playerVehScoring.mEstimatedLapTime:N3}\n"
                  + $"{playerVehScoring.mTimeBehindNext:N3}\n"
                  + $"{playerVehScoring.mTimeBehindLeader:N3}\n"
                  + $"{MainForm.GetStringFromBytes(playerVehScoring.mPitGroup)}\n"
                  + $"{playerVehScoring.mLapDist:N3}\n"
                  + $"{lapDistEstimated:N3}\n"
                  + $"{yaw:N3}\n"
                  + $"{pitch:N3}\n"
                  + $"{roll:N3}\n"
                  + string.Format("{0:n3} m/s {1:n4} km/h\n", speed, speed * 3.6));

                // Col2 values
                //g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 120, currY);

                if (this.logLightMode)
                    return;

                // Branch of UI choice: origin center or car# center
                // Fix rotation on car of choice or no.
                // Draw axes
                // Scale will be parameter, scale applied last on render to zoom.
                float scale = this.scale;

                var xVeh = (float)playerVeh.mPos.x;
                var zVeh = (float)playerVeh.mPos.z;
                var yawVeh = yaw;

                // View center
                //var xScrOrigin = this.view.Width / 2.0f;
                //var yScrOrigin = this.view.Height / 2.0f;
                //if (!this.centerOnVehicle)
                //{
                //  // Set world origin.
                //  g.TranslateTransform(xScrOrigin, yScrOrigin);
                //  this.RenderOrientationAxis(g);
                //  g.ScaleTransform(scale, scale);

                //  RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

                //  for (int i = 0; i < this.telemetry.mNumVehicles; ++i)
                //  {
                //    if (i == resolvedPlayerIdx)
                //      continue;

                //    var veh = this.telemetry.mVehicles[i];
                //    var thisYaw = Math.Atan2(veh.mOri[2].x, veh.mOri[2].z);
                //    this.RenderCar(g,
                //      (float)veh.mPos.x,
                //      -(float)veh.mPos.z,
                //      -(float)thisYaw, Brushes.Red);
                //  }
                //}
                //else
                //{
                //  g.TranslateTransform(xScrOrigin, yScrOrigin);

                //  if (this.rotateAroundVehicle)
                //    g.RotateTransform(180.0f + (float)yawVeh * DEGREES_IN_RADIAN);

                //  this.RenderOrientationAxis(g);
                //  g.ScaleTransform(scale, scale);
                //  g.TranslateTransform(-xVeh, zVeh);

                //  RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

                //  for (int i = 0; i < this.telemetry.mNumVehicles; ++i)
                //  {
                //    if (i == resolvedPlayerIdx)
                //      continue;

                //    var veh = this.telemetry.mVehicles[i];
                //    var thisYaw = Math.Atan2(veh.mOri[2].x, veh.mOri[2].z);
                //    this.RenderCar(g,
                //      (float)veh.mPos.x,
                //      -(float)veh.mPos.z,
                //      -(float)thisYaw, Brushes.Red);
                //  }
                //}
            }
        }

        public static rF2VehicleScoring GetPlayerScoring(ref rF2Scoring scoring)
        {
            var playerVehScoring = new rF2VehicleScoring();
            for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; ++i)
            {
                var vehicle = scoring.mVehicles[i];
                switch ((rFactor2Constants.rF2Control)vehicle.mControl)
                {
                    case rFactor2Constants.rF2Control.AI:
                    case rFactor2Constants.rF2Control.Player:
                    case rFactor2Constants.rF2Control.Remote:
                        if (vehicle.mIsPlayer == 1)
                            playerVehScoring = vehicle;

                        break;

                    default:
                        continue;
                }

                if (playerVehScoring.mIsPlayer == 1)
                    break;
            }

            return playerVehScoring;
        }


        // Length
        // 174.6in (4,435mm)
        // 175.6in (4,460mm) (Z06, ZR1)
        // Width
        // 72.6in (1,844mm)
        // 75.9in (1,928mm) (Z06, ZR1)
        /*PointF[] carPoly =
        {
            new PointF(0.922f, 2.217f),
            new PointF(0.922f, -1.4f),
            new PointF(1.3f, -1.4f),
            new PointF(0.0f, -2.217f),
            new PointF(-1.3f, -1.4f),
            new PointF(-0.922f, -1.4f),
            new PointF(-0.922f, 2.217f),
          };*/

        PointF[] carPoly =
        {
      new PointF(-0.922f, -2.217f),
      new PointF(-0.922f, 1.4f),
      new PointF(-1.3f, 1.4f),
      new PointF(0.0f, 2.217f),
      new PointF(1.3f, 1.4f),
      new PointF(0.922f, 1.4f),
      new PointF(0.922f, -2.217f),
    };

        private void RenderCar(Graphics g, float x, float y, float yaw, Brush brush)
        {
            var state = g.Save();

            g.TranslateTransform(x, y);

            g.RotateTransform(yaw * DEGREES_IN_RADIAN);

            g.FillPolygon(brush, this.carPoly);

            g.Restore(state);
        }

        static float arrowSide = 10.0f;
        PointF[] arrowHead =
        {
      new PointF(-arrowSide / 2.0f, -arrowSide / 2.0f),
      new PointF(0.0f, arrowSide / 2.0f),
      new PointF(arrowSide / 2.0f, -arrowSide / 2.0f)
    };

        //private void RenderOrientationAxis(Graphics g)
        //{

        //    float length = 1000.0f;
        //    float arrowDistX = this.view.Width / 2.0f - 10.0f;
        //    float arrowDistY = this.view.Height / 2.0f - 10.0f;

        //    // X (x screen) axis
        //    g.DrawLine(Pens.Red, -length, 0.0f, length, 0.0f);
        //    var state = g.Save();
        //    g.TranslateTransform(this.rotateAroundVehicle ? arrowDistY : arrowDistX, 0.0f);
        //    g.RotateTransform(-90.0f);
        //    g.FillPolygon(Brushes.Red, this.arrowHead);
        //    g.RotateTransform(90.0f);
        //    //g.DrawString("x+", SystemFonts.DefaultFont, Brushes.Red, -10.0f, 10.0f);
        //    g.Restore(state);

        //    state = g.Save();
        //    // Z (y screen) axis
        //    g.DrawLine(Pens.Blue, 0.0f, -length, 0.0f, length);
        //    g.TranslateTransform(0.0f, -arrowDistY);
        //    g.RotateTransform(180.0f);
        //    g.FillPolygon(Brushes.Blue, this.arrowHead);
        //    //g.DrawString("z+", SystemFonts.DefaultFont, Brushes.Blue, 10.0f, -10.0f);

        //    g.Restore(state);
        //}

        private void ConnectTimer_Tick(object sender, EventArgs e)
        {
            if (!this.connected)
            {
                try
                {
                    this.telemetryBuffer.Connect();
                    this.scoringBuffer.Connect();
                    this.rulesBuffer.Connect();
                    this.extendedBuffer.Connect();

                    this.connected = true;
                }
                catch (Exception)
                {
                    Disconnect();
                }
            }
        }

        private void DisconnectTimer_Tick(object sender, EventArgs e)
        {
            if (!this.connected)
                return;

            try
            {
                // Alternatively, I could release resources and try re-acquiring them immidiately.
                var processes = Process.GetProcessesByName(FuelOverlay.rFactor2Constants.RFACTOR2_PROCESS_NAME);
                if (processes.Length == 0)
                    Disconnect();
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        private void Disconnect()
        {
            this.extendedBuffer.Disconnect();
            this.scoringBuffer.Disconnect();
            this.rulesBuffer.Disconnect();
            this.telemetryBuffer.Disconnect();

            this.connected = false;
        }

        void LoadConfig()
        {
            float result = 0.0f;

            int posX = 0;
            int posY = 0;
            if (int.TryParse(this.config.Read("posX"), out posX) && int.TryParse(this.config.Read("posY"), out posY))
                this.Location = new Point(posX, posY);

            this.scale = 2.0f;
            if (float.TryParse(this.config.Read("scale"), out result))
                this.scale = result;

            if (this.scale <= 0.0f)
                this.scale = 0.1f;

            result = 0.0f;
            this.xOffset = 0.0f;
            if (float.TryParse(this.config.Read("xOffset"), out result))
                this.xOffset = result;

            result = 0.0f;
            this.yOffset = 0.0f;
            if (float.TryParse(this.config.Read("yOffset"), out result))
                this.yOffset = result;

            int intResult = 0;
            this.focusVehicle = 0;
            if (int.TryParse(this.config.Read("focusVehicle"), out intResult) && intResult >= 0)
                this.focusVehicle = intResult;

            intResult = 0;
            this.rotateAroundVehicle = false;
            if (int.TryParse(this.config.Read("rotateAroundVehicle"), out intResult) && intResult == 0)
                this.rotateAroundVehicle = false;

            intResult = 0;
            this.logLightMode = false;
            if (int.TryParse(this.config.Read("logLightMode"), out intResult) && intResult == 1)
                this.logLightMode = false;

            intResult = 0;
            this.logTiming = false;
            if (int.TryParse(this.config.Read("logTiming"), out intResult) && intResult == 0)
                this.logTiming = false;

            intResult = 0;
            TransitionTracker.useTeamName = false;
            if (int.TryParse(this.config.Read("useTeamNames"), out intResult) && intResult == 1)
                TransitionTracker.useTeamName = intResult != 0;

            intResult = 0;
            MainForm.useStockCarRulesPlugin = false;
            // Disable this option for now, it might come in handy down the line.
            //if (int.TryParse(this.config.Read("useStockCarRules"), out intResult) && intResult == 1)
            // MainForm.useStockCarRulesPlugin = true;

            //this.checkBoxStockCarRules.Checked = MainForm.useStockCarRulesPlugin;
        }

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [System.Runtime.InteropServices.DllImportAttribute("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [System.Runtime.InteropServices.DllImportAttribute("user32.dll")]
        public static extern bool ReleaseCapture();

        private void view_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }

            if (e.Button == MouseButtons.Right)
            {
                contextMenuStrip1.Show(this, new Point(0, 0));
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.config.Write("posX", this.Location.X.ToString());
            this.config.Write("posY", this.Location.Y.ToString());
            Application.Exit();
        }
    }
}
