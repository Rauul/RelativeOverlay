/*
TransitionTracker class various state transitions in rF2 state and optionally logs transitions to files.

Author: The Iron Wolf  (vleonavicius@hotmail.com)
Website: thecrewchief.org
*/
using RelativeOverlay.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace RelativeOverlay
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
            int playerSlot = -1;
            int LMP1s = 0;
            int LMP2s = 0;
            int LMP3s = 0;
            int LMGTs = 0;
            int GT3s = 0;

            var opponentInfos = new List<OpponentTimingInfo>();
            for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; ++i)
            {
                var veh = scoring.mVehicles[i];
                var o = new OpponentTimingInfo();
                o.mID = veh.mID;
                o.name = TransitionTracker.GetStringFromBytes(veh.mDriverName);
                o.position = veh.mPlace;

                o.lastS1Time = veh.mLastSector1 > 0.0 ? veh.mLastSector1 : -1.0;
                o.lastS2Time = veh.mLastSector1 > 0.0 && veh.mLastSector2 > 0.0
                  ? veh.mLastSector2 - veh.mLastSector1 : -1.0;
                o.lastS3Time = veh.mLastSector2 > 0.0 && veh.mLastLapTime > 0.0
                  ? veh.mLastLapTime - veh.mLastSector2 : -1.0;

                o.currS1Time = o.lastS1Time;
                o.currS2Time = o.lastS2Time;
                o.currS3Time = o.lastS3Time;

                // Check if we have more current values for S1 and S2.
                // S3 always equals to lastS3Time.
                if (veh.mCurSector1 > 0.0)
                    o.currS1Time = veh.mCurSector1;

                if (veh.mCurSector1 > 0.0 && veh.mCurSector2 > 0.0)
                    o.currS2Time = veh.mCurSector2 - veh.mCurSector1;

                o.bestS1Time = veh.mBestSector1 > 0.0 ? veh.mBestSector1 : -1.0;
                o.bestS2Time = veh.mBestSector1 > 0.0 && veh.mBestSector2 > 0.0 ? veh.mBestSector2 - veh.mBestSector1 : -1.0;

                // Wrong:
                o.bestS3Time = veh.mBestSector2 > 0.0 && veh.mBestLapTime > 0.0 ? veh.mBestLapTime - veh.mBestSector2 : -1.0;

                o.currLapET = veh.mLapStartET;
                o.lastLapTime = veh.mLastLapTime;
                o.currLapTime = scoring.mScoringInfo.mCurrentET - veh.mLapStartET;
                o.bestLapTime = veh.mBestLapTime;
                o.currLap = veh.mTotalLaps;
                o.currLapDistance = veh.mLapDist;
                o.totalLapDistance = veh.mTotalLaps + (veh.mLapDist / scoring.mScoringInfo.mLapDist);
                o.vehicleName = TransitionTracker.GetStringFromBytes(veh.mVehicleName);
                o.vehicleClass = TransitionTracker.GetStringFromBytes(veh.mVehicleClass);

                o.timeIntoLap = veh.mTimeIntoLap;
                o.estimatedLapTime = veh.mEstimatedLapTime;

                o.inPits = veh.mInPits == 1;
                o.inGarageStall = veh.mInGarageStall == 1;

                o.isPlayer = veh.mIsPlayer == 1;

                if (o.isPlayer)
                    playerSlot = opponentInfos.Count;

                opponentInfos.Add(o);
            }

            // Sort by position and set class position
            opponentInfos.Sort((o1, o2) => o1.position.CompareTo(o2.position));
            for (int i = 0; i < opponentInfos.Count; i++)
            {
                switch (opponentInfos[i].vehicleClass)
                {
                    case "LMP1":
                        LMP1s++;
                        opponentInfos[i].positionInClass = LMP1s;
                        break;
                    case "LMP2":
                        LMP2s++;
                        opponentInfos[i].positionInClass = LMP2s;
                        break;
                    case "LMP3":
                        LMP3s++;
                        opponentInfos[i].positionInClass = LMP3s;
                        break;
                    case "GTE":
                        LMGTs++;
                        opponentInfos[i].positionInClass = LMGTs;
                        break;
                    case "GT3":
                        GT3s++;
                        opponentInfos[i].positionInClass = GT3s;
                        break;
                    default:
                        break;
                }
            }

            // Remove cars in garage stall
            int c = 0;
            while (c < opponentInfos.Count)
            {
                if (opponentInfos[c].inGarageStall && !opponentInfos[c].isPlayer)
                {
                    opponentInfos.RemoveAt(c);
                    continue;
                }
                c++;
            }

            // Get player slot
            for (int i = 0; i < opponentInfos.Count; i++)
            {
                if (opponentInfos[i].isPlayer)
                {
                    playerSlot = i;
                    break;
                }
            }

            if (playerSlot == -1)
                return;

            // Calculate the relative distance to the player
            for (int i = 0; i < opponentInfos.Count; i++)
            {
                if (opponentInfos[i].isPlayer)
                {
                    opponentInfos[i].relativeDistance = 0.0;
                    opponentInfos[i].relativeTime = 0.0;
                    continue;
                }

                // If the vehicle is on the first half of the lap
                if (opponentInfos[playerSlot].currLapDistance < scoring.mScoringInfo.mLapDist / 2)
                {
                    if (opponentInfos[i].currLapDistance < opponentInfos[playerSlot].currLapDistance || opponentInfos[i].currLapDistance < opponentInfos[playerSlot].currLapDistance + scoring.mScoringInfo.mLapDist / 2)
                        opponentInfos[i].relativeDistance = opponentInfos[playerSlot].currLapDistance - opponentInfos[i].currLapDistance;
                    else
                        opponentInfos[i].relativeDistance = scoring.mScoringInfo.mLapDist - opponentInfos[i].currLapDistance + opponentInfos[playerSlot].currLapDistance;
                }
                else
                    if (opponentInfos[i].currLapDistance > opponentInfos[playerSlot].currLapDistance || opponentInfos[i].currLapDistance > opponentInfos[playerSlot].currLapDistance - scoring.mScoringInfo.mLapDist / 2)
                {
                    opponentInfos[i].relativeDistance = opponentInfos[playerSlot].currLapDistance - opponentInfos[i].currLapDistance;
                }
                else
                {
                    opponentInfos[i].relativeDistance = -Math.Abs(opponentInfos[playerSlot].currLapDistance - scoring.mScoringInfo.mLapDist - opponentInfos[i].currLapDistance);
                }

                // Calculate the reative time to driver
                if (opponentInfos[playerSlot].timeIntoLap < opponentInfos[playerSlot].estimatedLapTime / 2)
                {
                    if (opponentInfos[i].timeIntoLap < opponentInfos[playerSlot].timeIntoLap || opponentInfos[i].timeIntoLap < opponentInfos[playerSlot].timeIntoLap + opponentInfos[i].estimatedLapTime / 2)
                        opponentInfos[i].relativeTime = opponentInfos[playerSlot].timeIntoLap - opponentInfos[i].timeIntoLap;
                    else
                        opponentInfos[i].relativeTime = opponentInfos[i].estimatedLapTime - opponentInfos[i].timeIntoLap + opponentInfos[playerSlot].timeIntoLap;
                }
                else
                    if (opponentInfos[i].timeIntoLap > opponentInfos[playerSlot].timeIntoLap || opponentInfos[i].timeIntoLap > opponentInfos[playerSlot].timeIntoLap - opponentInfos[i].estimatedLapTime / 2)
                {
                    opponentInfos[i].relativeTime = opponentInfos[playerSlot].timeIntoLap - opponentInfos[i].timeIntoLap;
                }
                else
                {
                    opponentInfos[i].relativeTime = -Math.Abs(opponentInfos[playerSlot].timeIntoLap - opponentInfos[playerSlot].estimatedLapTime - opponentInfos[i].timeIntoLap);
                }
            }

            int gridSize = 3;
            int TopGridPlace()
            {
                if (opponentInfos.Count <= gridSize * 2 + 1)
                    return 0;

                if (playerSlot <= gridSize)
                    return 0;

                if (playerSlot >= opponentInfos.Count - gridSize)
                    return opponentInfos.Count - (gridSize * 2 + 1);

                return playerSlot - gridSize;
            }

            // Order by relative time to player, ascending.
            opponentInfos.Sort((o1, o2) => o1.relativeTime.CompareTo(o2.relativeTime));

            //Get playerslot after sorting
            for (int i = 0; i < opponentInfos.Count; i++)
            {
                if (opponentInfos[i].isPlayer)
                {
                    playerSlot = i;
                    break;
                }
            }

            int gridStartNumber = TopGridPlace();
            //Trim the list
            if (opponentInfos.Count > gridSize * 2 + 1)
                opponentInfos.RemoveRange(0, gridStartNumber);
            if (opponentInfos.Count > gridSize * 2 + 1)
                opponentInfos.RemoveRange(gridSize * 2 + 1, opponentInfos.Count - (gridSize * 2 + 1));

            /////////////////////////////////////////////////////////////////////////////////////////////////////////
            var PlayerInPitsBrush = new SolidBrush(Color.FromArgb(170, 130, 25));
            var PlayerOnTrackBrush = new SolidBrush(Color.FromArgb(215, 160, 30));
            var EqualCarInPitsBrush = new SolidBrush(Color.FromArgb(128, 128, 128));
            var EqualCarOnTrackBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
            var FasterCarInPitsBrush = new SolidBrush(Color.FromArgb(150, 60, 60));
            var FasterCarOnTrackBrush = new SolidBrush(Color.FromArgb(220, 85, 85));
            var SlowerCarInPitsBrush = new SolidBrush(Color.FromArgb(30, 85, 130));
            var SlowerCarOnTrackBrush = new SolidBrush(Color.FromArgb(45, 135, 205));
            var session = scoring.mScoringInfo.mSession;

            Brush TextColor(int other, int player)
            {
                // if it's us, we want player text colors
                if (opponentInfos[other].isPlayer)
                {
                    if (opponentInfos[other].inPits)
                        return PlayerInPitsBrush;
                    return PlayerOnTrackBrush;
                }

                if (session > 9)
                {
                    // somthings up at the start of a race so lock to equal car colors
                    if (opponentInfos[player].currLap < 0)
                    {
                        if (opponentInfos[other].inPits)
                            return EqualCarInPitsBrush;
                        return EqualCarOnTrackBrush;
                    }

                    // if a faster car is >.5 lap ahead of us, we want faster car colors
                    if ((opponentInfos[other].position < opponentInfos[player].position && opponentInfos[other].relativeTime > 0) ||
                        (opponentInfos[other].position < opponentInfos[player].position && opponentInfos[other].totalLapDistance > opponentInfos[player].totalLapDistance + 1))
                    {
                        if (opponentInfos[other].inPits)
                            return FasterCarInPitsBrush;
                        return FasterCarOnTrackBrush;
                    }

                    // else if a slower car is >.5 lap behind us, we want slower car colors
                    else if ((opponentInfos[other].position > opponentInfos[player].position && opponentInfos[other].relativeTime < 0) ||
                        (opponentInfos[other].position > opponentInfos[player].position && opponentInfos[other].totalLapDistance < opponentInfos[player].totalLapDistance - 1))
                    {
                        if (opponentInfos[other].inPits)
                            return SlowerCarInPitsBrush;
                        return SlowerCarOnTrackBrush;
                    }

                    // else we fight for position and want equal car colors
                    else
                    {
                        if (opponentInfos[other].inPits)
                            return EqualCarInPitsBrush;
                        return EqualCarOnTrackBrush;
                    }
                }

                // if not in race we want white
                else
                {
                    if (opponentInfos[other].inPits)
                        return EqualCarInPitsBrush;
                    return EqualCarOnTrackBrush;
                }
            }

            Brush Classcolor(int i)
            {
                if (opponentInfos[i].vehicleClass == "LMP1")
                    return new SolidBrush(Color.FromArgb(210, 40, 40));

                if (opponentInfos[i].vehicleClass == "LMP2")
                    return new SolidBrush(Color.FromArgb(45, 140, 230));

                if (opponentInfos[i].vehicleClass == "LMP3")
                    return new SolidBrush(Color.FromArgb(200, 100, 220));

                if (opponentInfos[i].vehicleClass == "GTE")
                    return new SolidBrush(Color.FromArgb(35, 180, 75));

                if (opponentInfos[i].vehicleClass == "GT3")
                    return new SolidBrush(Color.FromArgb(255, 190, 110));

                return new SolidBrush(Color.FromArgb(208, 103, 28));
            }

            if (g != null)
            {
                for (int i2 = 0; i2 < opponentInfos.Count; i2++)
                {
                    if (opponentInfos[i2].isPlayer)
                    {
                        playerSlot = i2;
                        break;
                    }
                }

                var point = new Point(25, 5);
                var rAlinged = new StringFormat() { Alignment = StringAlignment.Far };
                var lAlinged = new StringFormat() { Alignment = StringAlignment.Near };
                var font = new Font("Verdana", 10, FontStyle.Bold);

                int i = 0;
                foreach (var o in opponentInfos)
                {
                    var brush = TextColor(i, playerSlot);
                    g.DrawString(o.position.ToString(), font, brush, point, rAlinged);
                    point.X += 5;
                    g.FillRectangle(Classcolor(i), point.X, point.Y, 25, 18);
                    point.X += 25;
                    g.DrawString(o.positionInClass.ToString(), font, Brushes.Black, point, rAlinged);
                    point.X += 5;
                    if (useTeamName)
                        g.DrawString(o.vehicleName, font, brush, point, lAlinged);
                    else
                        g.DrawString(o.name, font, brush, point, lAlinged);
                    point.X += 335;
                    g.DrawString((o.relativeTime * -1).ToString("0.0"), font, brush, point, rAlinged);

                    point.X = 25;
                    point.Y += 20;
                    i++;
                }
            }
        }
    }
}
