// Integration test of the built-in engine with a real llama-server and a tiny random model.
// Usage: QALAM_LLAMA_SERVER=/path/llama-server dotnet run -- tiny.gguf
using Qalam.Core;

int failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {name}"); if (!ok) failures++; }
var model = args[0];

var eng = new LlamaEngine { UnloadAfterMinutes = 0 };
Check($"server found: {LlamaEngine.ServerPath}", LlamaEngine.Available);
var first = await eng.CompleteAsync(model, "Hello the", 8, default);
Check("first request returns at once while the model loads", first == null && eng.State == LlamaEngine.EngineState.Loading);
for (int i = 0; i < 120 && eng.State == LlamaEngine.EngineState.Loading; i++) await Task.Delay(250);
Check($"model loaded (state {eng.State} {eng.Error})", eng.State == LlamaEngine.EngineState.Ready);
var r = await eng.CompleteAsync(model, "Hello the", 8, default);
Check($"completion returned text ({r?.Length} chars)", !string.IsNullOrEmpty(r));
Check($"memory reported: {ModelFiles.Size(eng.MemoryBytes)}", eng.MemoryBytes > 0);

File.WriteAllText(Path.Combine(Path.GetTempPath(), "qalam-broken.gguf"), "not a model");
var broken = new LlamaEngine();
await broken.CompleteAsync(Path.Combine(Path.GetTempPath(), "qalam-broken.gguf"), "x", 4, default);
for (int i = 0; i < 80 && broken.State == LlamaEngine.EngineState.Loading; i++) await Task.Delay(250);
Check($"broken file reported: {broken.Error}", broken.State == LlamaEngine.EngineState.Failed);

var missing = new LlamaEngine();
await missing.CompleteAsync("/nonexistent/model.gguf", "x", 4, default);
Check($"missing file reported: {missing.Error}", missing.State == LlamaEngine.EngineState.Failed);

double lastProgress = 0;
var copied = await ModelFiles.CopyToQalamAsync(model, new Progress<double>(f => lastProgress = f), default);
Check($"model copied to Qalam folder: {copied}", File.Exists(copied) && new FileInfo(copied).Length == new FileInfo(model).Length && ModelFiles.SourceOf(copied) == "Qalam");
File.Delete(copied);
Check("Cotypist path recognised", ModelFiles.SourceOf("/Users/x/Library/Application Support/Cotypist/Models/gemma.gguf") == "Cotypist");

eng.Stop();
await Task.Delay(500);
var alive = System.Diagnostics.Process.GetProcessesByName("llama-server").Length;
Check($"no engine process left after Stop (alive: {alive})", alive == 0);

Console.WriteLine(failures == 0 ? "ENGINE OK" : $"ENGINE FAILED ({failures})");
return failures == 0 ? 0 : 1;
