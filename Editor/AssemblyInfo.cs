using System.Runtime.CompilerServices;

// expose internal parser helpers (MCPHandlers.Edit.cs) to the EditMode test assembly
[assembly: InternalsVisibleTo("MCPBridge.Editor.Tests")]
