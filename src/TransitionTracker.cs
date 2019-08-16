/*
TransitionTracker class various state transitions in rF2 state and optionally logs transitions to files.

Author: The Iron Wolf  (vleonavicius@hotmail.com)
Website: thecrewchief.org
*/
using FuelOverlay.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;
using System.Text;

namespace FuelOverlay
{
    internal class TransitionTracker
    {
        public static bool useTeamName = false;

        internal TransitionTracker()
        {
        }

        public static string GetSessionString(int session)
        {
            // current session (0=testday 1-4=practice 5-8=qual 9=warmup 10-13=race)
            if (session == 0)
                return $"TestDay({session})";
            else if (session >= 1 && session <= 4)
                return $"Practice({session})";
            else if (session >= 5 && session <= 8)
                return $"Qualification({session})";
            else if (session == 9)
                return $"WarmUp({session})";
            else if (session >= 10 && session <= 13)
                return $"Race({session})";

            return $"Unknown({session})";
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

        internal class OpponentTimingInfo
        {
            internal string name = null;
            internal int position = -1;
            internal int positionInClass = -1;
            internal bool isPlayer = false;
            internal bool inPits = false;
            internal bool inGarageStall = false;

            internal double lastS1Time = -1.0;
            internal double lastS2Time = -1.0;
            internal double lastS3Time = -1.0;

            internal double currS1Time = -1.0;
            internal double currS2Time = -1.0;
            internal double currS3Time = -1.0;

            internal double bestS1Time = -1.0;
            internal double bestS2Time = -1.0;
            internal double bestS3Time = -1.0;

            internal double currLapET = -1.0;
            internal double lastLapTime = -1.0;
            internal double currLapTime = -1.0;
            internal double bestLapTime = -1.0;

            internal double timeIntoLap = -1.0;
            internal double estimatedLapTime = -1.0;

            internal int currLap = -1;
            internal double currLapDistance = -1.0;
            internal double totalLapDistance = -1.0;
            internal double relativeDistance = -1.0;
            internal double relativeTime = -1.0;

            internal string vehicleName = null;
            internal string vehicleClass = null;

            internal long mID = -1;
        }

        
        internal void TrackTimings(ref rF2Scoring scoring, ref rF2Telemetry telemetry, ref rF2Rules rules, ref rF2Extended extended, Graphics g, bool logToFile)
        {
            
        }
    }
}
