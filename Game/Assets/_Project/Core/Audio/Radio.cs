using System;
using System.Collections.Generic;

namespace HeroGame.Core.Audio
{
    public enum StationFormat
    {
        Music,
        News,
    }

    [Serializable]
    public sealed class RadioTrack
    {
        public string Id = "";
        public string Title = "";
        public string Artist = "";
        public int Seconds = 180;
    }

    /// <summary>A radio station (radio_stations.json). Music is original and, until commissioned, metadata only (ASSET_TRACKER).</summary>
    [Serializable]
    public sealed class RadioStation
    {
        public string Id = "";
        public string Name = "";
        public string Frequency = "";
        public StationFormat Format;
        public string Genre = "";
        public string Host = "";
        public bool NewsAtTopOfHour = true;
        public int AdsPerHour = 3;
        public int TalkPerHour = 2;
        /// <summary>Host lines with tokens {time} {weather} {temp} {city} {district} {headline}.</summary>
        public List<string> Talk = new List<string>();
        public List<RadioTrack> Tracks = new List<RadioTrack>();
    }

    public enum SegmentKind
    {
        Track,
        News,
        Weather,
        Ad,
        Talk,
        EmergencyAlert,
    }

    /// <summary>One item on air: a song, a bulletin, an ad, a host break or an emergency alert.</summary>
    public sealed class RadioSegment
    {
        public string Station = "";
        public SegmentKind Kind;
        public long StartSecond;
        public long EndSecond;
        public string Title = "";
        /// <summary>What is said (subtitles for talk, news, ads and alerts; artist for tracks).</summary>
        public string Text = "";
        public string TrackId = "";

        public int Duration => (int)(EndSecond - StartSecond);
    }
}
