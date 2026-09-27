using System.Linq;
using HeroGame.Core.Audio;
using HeroGame.Core.Civic;
using HeroGame.Core.Economy;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class RadioTests
    {
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("radio", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
        }

        [Test]
        public void EveryStation_FillsTheHourWithContiguousSegments()
        {
            Assert.GreaterOrEqual(_world.Radio.Stations.Count, 4);
            var hour = _world.Radio.HourAt(_world.Clock.Now);
            foreach (var station in _world.Radio.Stations)
            {
                var schedule = _world.Radio.Schedule(station.Id, hour);
                Assert.IsNotEmpty(schedule, station.Id);
                Assert.AreEqual(hour * GameDateTime.SecondsPerHour, schedule[0].StartSecond);
                Assert.AreEqual((hour + 1) * GameDateTime.SecondsPerHour, schedule[schedule.Count - 1].EndSecond, station.Id + " fills the hour");
                for (var i = 1; i < schedule.Count; i++) Assert.AreEqual(schedule[i - 1].EndSecond, schedule[i].StartSecond, "no gaps or overlaps");
                Assert.IsTrue(schedule.All(s => s.Duration > 0));
                Assert.IsFalse(schedule.Any(s => s.Text.Contains("{")), "tokens are filled");
                if (station.Format == StationFormat.Music) Assert.Greater(schedule.Count(s => s.Kind == SegmentKind.Track), 5);
                if (station.NewsAtTopOfHour) Assert.AreEqual(SegmentKind.News, schedule[0].Kind);
            }
        }

        [Test]
        public void OnAir_IsDeterministic_AcrossWorlds()
        {
            var other = WorldGenerator.Create("radio", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var t = _world.Clock.Now.AddMinutes(37);
            foreach (var station in _world.Radio.Stations)
            {
                var a = _world.Radio.OnAir(station.Id, t);
                var b = other.Radio.OnAir(station.Id, t);
                Assert.AreEqual(a.Title, b.Title);
                Assert.AreEqual(a.StartSecond, b.StartSecond);
                var rs = _world.Radio.RadioSecond(t);
                Assert.IsTrue(rs >= a.StartSecond && rs < a.EndSecond);
            }
            Assert.IsNull(_world.Radio.OnAir("no_such_station", t));
        }

        [Test]
        public void Songs_LastTheirRealLength_WhileGameTimeRunsFaster()
        {
            var song = _world.Radio.Schedule("bayou_gold", _world.Radio.HourAt(_world.Clock.Now)).First(s => s.Kind == SegmentKind.Track);
            var track = _world.Radio.Station("bayou_gold").Tracks.First(t => t.Id == song.TrackId);
            Assert.AreEqual(track.Seconds, song.Duration);
            var gameStart = new GameDateTime((long)(song.StartSecond * _world.Clock.TimeScale) + 1);
            Assert.AreEqual(song.TrackId, _world.Radio.OnAir("bayou_gold", gameStart.AddSeconds((long)((track.Seconds - 2) * _world.Clock.TimeScale))).TrackId,
                "a song that started is still playing after (length − 2) real seconds");
        }

        [Test]
        public void News_ComesFromRealHistory()
        {
            _world.History.Record(_world.Today, HistoryCategory.Politics, 5, "Council approves the Bayou Flood Control Bond");
            var news = _world.Radio.Schedule("arden_public", _world.Radio.HourAt(_world.Clock.Now) + 1).First(s => s.Kind == SegmentKind.News);
            StringAssert.Contains("Flood Control Bond", news.Text);
        }

        [Test]
        public void Advertisers_GetAirtime()
        {
            var advertiser = _world.Businesses.Values.OrderBy(b => b.Id).First();
            foreach (var b in _world.Businesses.Values) b.AdvertisingCents = 0;
            advertiser.AdvertisingCents = 500000;
            var ads = Enumerable.Range(1, 24)
                .SelectMany(h => _world.Radio.Schedule("the_current", _world.Radio.HourAt(_world.Clock.Now) + h))
                .Where(s => s.Kind == SegmentKind.Ad).ToList();
            Assert.IsNotEmpty(ads);
            Assert.Greater(ads.Count(a => a.Title == advertiser.Name) / (double)ads.Count, 0.3, "paying for ads buys a big share of airtime");
        }

        [Test]
        public void Emergencies_CutIntoEveryStation()
        {
            var district = _world.Geography.Districts.OrderBy(d => d.Id).First(d => d.Type == DistrictType.Industrial || d.Type == DistrictType.Port);
            _world.Calendar.Trigger(DisasterKind.ChemicalIncident, district, _world.Clock.Now);
            var next = _world.Radio.HourAt(_world.Clock.Now) + 1;
            foreach (var station in _world.Radio.Stations)
            {
                var first = _world.Radio.Schedule(station.Id, next)[0];
                Assert.AreEqual(SegmentKind.EmergencyAlert, first.Kind, station.Id);
                StringAssert.Contains(district.Name, first.Text);
            }
        }
    }
}
