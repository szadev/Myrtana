using System;
using System.Collections.Generic;
using System.IO;
using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class KernelTests
    {
        struct Ping : IGameEvent { public int N; }
        struct Pong : IGameEvent { public int N; }

        [Test]
        public void ActorIdsAndIds_CompareAndHashByName_WithTheirCachedHash()
        {
            // perf-9: the hash is computed once in the constructor; equal names from different strings stay equal keys.
            var a = new ActorId("Gnar");
            var b = new ActorId(new string(new[] { 'G', 'n', 'a', 'r' }));
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b && !(a != b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a, Is.Not.EqualTo(new ActorId("gnar")), "ordinal, case-sensitive");
            Assert.That(default(ActorId), Is.EqualTo(new ActorId(null)));
            Assert.That(default(ActorId), Is.Not.EqualTo(new ActorId("")), "no name and an empty name stay different");
            Assert.That(new ActorId("player").IsPlayer, Is.True);
            var map = new Dictionary<ActorId, int> { [a] = 1 };
            Assert.That(map.TryGetValue(b, out int v) && v == 1, Is.True);

            var p = new Id<string>("Point_01");
            var q = new Id<string>(string.Concat("Point_", "01"));
            Assert.That(p, Is.EqualTo(q));
            Assert.That(p.GetHashCode(), Is.EqualTo(q.GetHashCode()));
            Assert.That(default(Id<string>).IsEmpty && default(Id<string>) == new Id<string>(null), Is.True);
            Assert.That(new HashSet<Id<string>> { p }.Contains(q), Is.True);
        }

        [Test]
        public void EventBus_QueuesEventsPublishedDuringDispatch()
        {
            var bus = new EventBus();
            var order = new List<string>();
            bus.Subscribe<Ping>(p => { order.Add("ping1"); bus.Publish(new Pong { N = p.N }); });
            bus.Subscribe<Ping>(p => order.Add("ping2"));
            bus.Subscribe<Pong>(p => order.Add("pong"));

            bus.Publish(new Ping { N = 1 });

            Assert.That(order, Is.EqualTo(new[] { "ping1", "ping2", "pong" }));
        }

        [Test]
        public void EventBus_UnsubscribeStopsDelivery()
        {
            var bus = new EventBus();
            int n = 0;
            var sub = bus.Subscribe<Ping>(_ => n++);
            bus.Publish(new Ping());
            sub.Dispose();
            bus.Publish(new Ping());
            Assert.That(n, Is.EqualTo(1));
        }

        [Test]
        public void HandlerRegistry_RejectsDuplicates()
        {
            var r = new HandlerRegistry<string, object>();
            r.Register("Say", new object());
            Assert.Throws<InvalidOperationException>(() => r.Register("Say", new object()));
            Assert.That(r.TryGet("Say", out _), Is.True);
            Assert.That(r.TryGet("Give", out _), Is.False);
        }

        [Test]
        public void SeededRandom_IsDeterministic_AndRestoresItsState()
        {
            var a = new SeededRandom(42);
            var b = new SeededRandom(42);
            for (int i = 0; i < 10; i++) Assert.That(a.Next(1000), Is.EqualTo(b.Next(1000)));
            var state = a.Save();
            int next = a.Next(1000);
            float f = a.NextFloat();
            a.Load(state);
            Assert.That(a.Next(1000), Is.EqualTo(next));
            Assert.That(a.NextFloat(), Is.EqualTo(f));
            Assert.That(f, Is.InRange(0f, 1f));
        }

        [Test]
        public void GameClock_AdvancesByTimeScale_AndWrapsDays()
        {
            var clock = new GameClock(startHour: 22f, timeScale: 60f);
            clock.Advance(180f); // 3 game hours
            Assert.That(clock.Day, Is.EqualTo(1));
            Assert.That(clock.HourOfDay, Is.EqualTo(1f).Within(1e-4));
        }

        [Test]
        public void EmptySave_RoundTripsToIdenticalBytes()
        {
            var header = new SaveHeader(new[] { new PackStamp("Gothic3", "abc") }, 12.5);
            var first = new MemoryStream();
            SaveFile.Write(first, header, new ISaveParticipant[0]);

            first.Position = 0;
            var read = SaveFile.Read(first, new ISaveParticipant[0]);
            var second = new MemoryStream();
            SaveFile.Write(second, read, new ISaveParticipant[0]);

            Assert.That(read.PlaySeconds, Is.EqualTo(12.5));
            Assert.That(read.Packs[0].Id, Is.EqualTo("Gothic3"));
            Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
        }

        sealed class Counter : ISaveParticipant
        {
            public string Name = "counter";
            public int Value;
            public string Section => Name;
            public int Version => 3;
            public int ReadVersion;
            public void Write(ISaveWriter w) { w.WriteInt32(Value); w.WriteString(null); }
            public void Read(ISaveReader r, int version) { Value = r.ReadInt32(); Assert.That(r.ReadString(), Is.Null); ReadVersion = version; }
        }

        [Test]
        public void Save_SectionsRoundTrip_AndUnknownSectionsAreSkipped()
        {
            var stream = new MemoryStream();
            SaveFile.Write(stream, new SaveHeader(null, 0), new ISaveParticipant[] { new Counter { Name = "old", Value = 1 }, new Counter { Value = 7 } });
            stream.Position = 0;
            var target = new Counter();
            var log = new ListLog();

            SaveFile.Read(stream, new ISaveParticipant[] { target }, log);

            Assert.That(target.Value, Is.EqualTo(7));
            Assert.That(target.ReadVersion, Is.EqualTo(3));
            Assert.That(log.Messages, Has.Some.Contains("old"));
        }
    }
}
