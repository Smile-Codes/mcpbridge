using System.Collections.Generic;
using NUnit.Framework;
using MCPBridge;

namespace MCPBridge.Tests
{
    // Unit tests for the run_batch JSON parsing helpers (MCPHandlers.Edit.cs) — the most logic-heavy
    // part of the Apply/Edit Pack. These also double as the dog-food smoke test for unity_run_tests.
    public class BatchParserTests
    {
        // ── ExtractJsonArrayRaw ───────────────────────────────────────────
        [Test]
        public void ExtractJsonArrayRaw_ReturnsArrayWithBrackets()
        {
            string body = "{\"command\":\"run_batch\",\"commands\":[{\"command\":\"a\"},{\"command\":\"b\"}]}";
            string arr = MCPHandlers.ExtractJsonArrayRaw(body, "commands");
            Assert.AreEqual("[{\"command\":\"a\"},{\"command\":\"b\"}]", arr);
        }

        [Test]
        public void ExtractJsonArrayRaw_IgnoresBracketInsideString()
        {
            string body = "{\"commands\":[{\"name\":\"a]b[c\"}]}";
            string arr = MCPHandlers.ExtractJsonArrayRaw(body, "commands");
            Assert.AreEqual("[{\"name\":\"a]b[c\"}]", arr);
        }

        [Test]
        public void ExtractJsonArrayRaw_HandlesEscapedQuoteInString()
        {
            // value contains an escaped quote then a ] — must not end the string early
            string body = "{\"commands\":[{\"v\":\"x\\\"]y\"}]}";
            string arr = MCPHandlers.ExtractJsonArrayRaw(body, "commands");
            Assert.AreEqual("[{\"v\":\"x\\\"]y\"}]", arr);
        }

        [Test]
        public void ExtractJsonArrayRaw_ReturnsNullWhenKeyMissing()
        {
            Assert.IsNull(MCPHandlers.ExtractJsonArrayRaw("{\"other\":[1,2]}", "commands"));
            Assert.IsNull(MCPHandlers.ExtractJsonArrayRaw("", "commands"));
        }

        // ── SplitTopLevelObjects ──────────────────────────────────────────
        [Test]
        public void SplitTopLevelObjects_SplitsTwoObjects()
        {
            var items = MCPHandlers.SplitTopLevelObjects("[{\"a\":1},{\"b\":2}]");
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("{\"a\":1}", items[0]);
            Assert.AreEqual("{\"b\":2}", items[1]);
        }

        [Test]
        public void SplitTopLevelObjects_HandlesNestedBraces()
        {
            var items = MCPHandlers.SplitTopLevelObjects("[{\"a\":{\"x\":1}},{\"b\":2}]");
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("{\"a\":{\"x\":1}}", items[0]);
            Assert.AreEqual("{\"b\":2}", items[1]);
        }

        [Test]
        public void SplitTopLevelObjects_IgnoresBraceInsideString()
        {
            var items = MCPHandlers.SplitTopLevelObjects("[{\"a\":\"}{,\"},{\"b\":2}]");
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("{\"a\":\"}{,\"}", items[0]);
        }

        [Test]
        public void SplitTopLevelObjects_EmptyArray()
        {
            Assert.AreEqual(0, MCPHandlers.SplitTopLevelObjects("[]").Count);
            Assert.AreEqual(0, MCPHandlers.SplitTopLevelObjects("").Count);
        }

        [Test]
        public void Extract_Then_Split_RoundTrip()
        {
            string body = "{\"command\":\"run_batch\",\"commands\":[" +
                          "{\"command\":\"create_gameobject\",\"name\":\"A\",\"primitive\":\"cube\"}," +
                          "{\"command\":\"set_transform\",\"name\":\"A\",\"set\":\"pos\",\"px\":2}" +
                          "]}";
            string arr = MCPHandlers.ExtractJsonArrayRaw(body, "commands");
            List<string> items = MCPHandlers.SplitTopLevelObjects(arr);
            Assert.AreEqual(2, items.Count);
            StringAssert.Contains("create_gameobject", items[0]);
            StringAssert.Contains("set_transform", items[1]);
        }

        // ── CountOccurrences / ReplaceFirst (edit_script primitives) ──────
        [Test]
        public void CountOccurrences_CountsNonOverlapping()
        {
            Assert.AreEqual(3, MCPHandlers.CountOccurrences("a.a.a", "a"));
            Assert.AreEqual(2, MCPHandlers.CountOccurrences("aaaa", "aa"));   // non-overlapping
            Assert.AreEqual(0, MCPHandlers.CountOccurrences("abc", "z"));
            Assert.AreEqual(0, MCPHandlers.CountOccurrences("abc", ""));
        }

        [Test]
        public void ReplaceFirst_ReplacesOnlyFirst()
        {
            Assert.AreEqual("X.a.a", MCPHandlers.ReplaceFirst("a.a.a", "a", "X"));
        }

        [Test]
        public void ReplaceFirst_ReturnsOriginalWhenNotFound()
        {
            Assert.AreEqual("abc", MCPHandlers.ReplaceFirst("abc", "z", "X"));
        }
    }
}
