"""
Quick check: can each local Ollama model call a tool reliably, and how fast?
Run:  python test_tool.py
"""
import time
import ollama

MODELS = ["qwen3.5:9b"]
RUNS_PER_MODEL = 4

tools = [{
    "type": "function",
    "function": {
        "name": "get_my_balance",
        "description": "Get the current user's account balance",
        "parameters": {"type": "object", "properties": {}, "required": []},
    },
}]

messages = [{"role": "user", "content": "What's my balance?"}]


def ask(model):
    try:
        # think=False turns off Qwen's long "thinking" output
        return ollama.chat(model=model, messages=messages, tools=tools, think=False)
    except TypeError:
        # older ollama Python package without the 'think' option
        return ollama.chat(model=model, messages=messages, tools=tools)


for model in MODELS:
    print(f"\n=== {model} ===")
    ok, times = 0, []
    for i in range(1, RUNS_PER_MODEL + 1):
        start = time.time()
        resp = ask(model)
        elapsed = time.time() - start
        times.append(elapsed)

        calls = resp.message.tool_calls or []
        names = [c.function.name for c in calls]
        success = "get_my_balance" in names
        ok += success

        print(f"Run {i}: {'OK  ' if success else 'MISS'} "
              f"tool_calls={names} time={elapsed:.1f}s")
        if not success and resp.message.content:
            print(f"        text: {resp.message.content[:120]!r}")

    # first run includes model loading time, so report it separately
    warm = times[1:] or times
    print(f"Result: {ok}/{RUNS_PER_MODEL} correct tool calls, "
          f"first run {times[0]:.1f}s (includes loading), "
          f"avg after that {sum(warm)/len(warm):.1f}s")

print("\nTip: while a model is loaded, run `ollama ps` to see if it uses GPU or CPU.")
