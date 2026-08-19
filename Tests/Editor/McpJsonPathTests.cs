using System.IO;
using NUnit.Framework;

namespace MCPBridge.Tests
{
    // Guards the two path rules behind <project>/.mcp.json (MCPServer.EnsureMcpJson):
    // where the args path points, and which files this package is allowed to rewrite.
    public class McpJsonPathTests
    {
        const string ProjectRoot = "C:/Games/MyProject";

        // ── McpArgsPath ───────────────────────────────────────────────────
        [Test]
        public void McpArgsPath_EmbeddedPackage_IsProjectRelative()
        {
            string entry = ProjectRoot + "/Packages/com.mcpbridge/Server~/index.js";
            Assert.AreEqual("./Packages/com.mcpbridge/Server~/index.js", MCPServer.McpArgsPath(entry, ProjectRoot));
        }

        [Test]
        public void McpArgsPath_PackageOutsideProject_IsAbsoluteWithForwardSlashes()
        {
            string entry = "C:/Work/git/com.mcpbridge/Server~/index.js";
            string args = MCPServer.McpArgsPath(entry, ProjectRoot);
            Assert.IsTrue(Path.IsPathRooted(args), args);
            Assert.IsFalse(args.Contains("\\"), args);
            Assert.IsTrue(args.EndsWith("/com.mcpbridge/Server~/index.js"), args);
        }

        [Test]
        public void McpArgsPath_ProjectRootWithTrailingSeparator_StillRelative()
        {
            string entry = ProjectRoot + "/Packages/com.mcpbridge/Server~/index.js";
            Assert.AreEqual("./Packages/com.mcpbridge/Server~/index.js", MCPServer.McpArgsPath(entry, ProjectRoot + "/"));
        }

        // ── GeneratedMcpJson ──────────────────────────────────────────────
        [Test]
        public void GeneratedMcpJson_KeepsArgsPathIntact()
        {
            string args = "C:/path with spaces/com.mcpbridge/Server~/index.js";
            StringAssert.Contains($"\"{args}\"", MCPServer.GeneratedMcpJson(args));
        }

        [Test]
        public void GeneratedMcpJson_IsRecognizedAsOwnedByThisPackage()
        {
            Assert.IsTrue(MCPServer.IsGeneratedByThisPackage(MCPServer.GeneratedMcpJson("./Packages/com.mcpbridge/Server~/index.js")));
        }

        // ── IsGeneratedByThisPackage: ตัดสินว่าไฟล์ไหนเขียนทับได้ ──────────
        [Test]
        public void IsGeneratedByThisPackage_LegacyHardcodedFile_IsOwned()
        {
            string legacy = "{\n  \"mcpServers\": {\n    \"unity\": {\n      \"command\": \"node\",\n"
                          + "      \"args\": [\"./Tools/unity-mcp-server/index.js\"]\n    }\n  }\n}\n";
            Assert.IsTrue(MCPServer.IsGeneratedByThisPackage(legacy));
        }

        [Test]
        public void IsGeneratedByThisPackage_CrlfLineEndings_IsOwned()
        {
            string crlf = MCPServer.GeneratedMcpJson("./Packages/com.mcpbridge/Server~/index.js").Replace("\n", "\r\n");
            Assert.IsTrue(MCPServer.IsGeneratedByThisPackage(crlf));
        }

        [Test]
        public void IsGeneratedByThisPackage_FileWithAnotherServer_IsNotOwned()
        {
            string userFile = "{\n  \"mcpServers\": {\n    \"postgres\": { \"command\": \"npx\", \"args\": [\"pg-mcp\"] },\n"
                            + "    \"unity\": { \"command\": \"node\", \"args\": [\"./Packages/com.mcpbridge/Server~/index.js\"] }\n  }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(userFile));
        }

        [Test]
        public void IsGeneratedByThisPackage_FileWithExtraKeys_IsNotOwned()
        {
            string userFile = "{\n  \"mcpServers\": {\n    \"unity\": { \"command\": \"node\", \"args\": [\"./x.js\"] }\n  },\n"
                            + "  \"permissions\": { \"allow\": [\"*\"] }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(userFile));
        }

        [Test]
        public void IsGeneratedByThisPackage_UnityEntryWithEnvBlock_IsNotOwned()
        {
            string withEnv = "{\n  \"mcpServers\": {\n    \"unity\": { \"command\": \"node\", \"env\": { \"UNITY_MCP_PORT\": \"23457\" },\n"
                           + "      \"args\": [\"./Packages/com.mcpbridge/Server~/index.js\"] }\n  }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(withEnv));
        }
    }
}
