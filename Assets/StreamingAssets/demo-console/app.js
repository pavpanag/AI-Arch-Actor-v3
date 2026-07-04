/* The Lamp — directing console. No build step, no eval: Preact (UMD) + htm, served by CNCDemoBridge. */
(function () {
  "use strict";
  var h = preact.h;
  var Fragment = preact.Fragment;
  var useState = preactHooks.useState, useEffect = preactHooks.useEffect, useRef = preactHooks.useRef;

  // --- tiny API helper ---------------------------------------------------
  function api(path, body) {
    return fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body)
    }).then(function (r) { return r.json().catch(function () { return {}; }); });
  }

  var STEPS = [
    { key: "dramaturgy", label: "Character" },
    { key: "scene",      label: "Scene" },
    { key: "expression", label: "Expression" },
    { key: "stage",      label: "Stage" }
  ];

  // --- a single editable question ---------------------------------------
  function Question(props) {
    return h("div", { className: "q" },
      h("label", null, props.q.question),
      h("textarea", {
        value: props.q.answer || "", name: "frame-answer-" + props.index,
        onInput: function (e) { props.onChange(props.index, e.target.value); }
      })
    );
  }

  // --- a frame step (Character or Scene) --------------------------------
  function FrameStep(props) {
    var kind = props.kind;                     // "dramaturgy" | "scene"
    var items = props.items;                   // [{question, answer}]
    var setItems = props.setItems;
    var followUpState = useState(""); var followUp = followUpState[0], setFollowUp = followUpState[1];
    var followAnsState = useState(""); var followAns = followAnsState[0], setFollowAns = followAnsState[1];
    var compiledState = useState(null); var compiled = compiledState[0], setCompiled = compiledState[1];
    var statusState = useState(""); var status = statusState[0], setStatus = statusState[1];
    var busyState = useState(false); var busy = busyState[0], setBusy = busyState[1];

    function edit(i, val) {
      var next = items.slice();
      next[i] = { question: next[i].question, answer: val };
      setItems(next);
    }

    function askFollowUp() {
      setBusy(true); setStatus("The lamp is considering what it still needs to know…");
      api("/api/frame/followup", { type: kind, items: items }).then(function (r) {
        setFollowUp((r && r.followUp) || "");
        setStatus((r && r.followUp) ? "" : "Nothing unclear — you can compile.");
        setBusy(false);
      });
    }

    function compile() {
      setBusy(true); setStatus("Compiling…");
      api("/api/frame/compile", {
        type: kind, items: items,
        followUpQuestion: followUp, followUpAnswer: followAns
      }).then(function (r) {
        setCompiled(r || {});
        setStatus("Applied. The lamp is playing this now.");
        setBusy(false);
      });
    }

    var isScene = kind === "scene";
    return h("div", { className: "card" },
      h("h2", null, isScene ? "The Scene" : "Who the Lamp Is"),
      h("p", { className: "lead" },
        isScene
          ? "Set the world the lamp is in — answered as the lamp, in its own voice."
          : "Answer as the lamp — in the first person. This becomes who it is."),
      items.map(function (q, i) { return h(Question, { key: i, q: q, index: i, onChange: edit }); }),
      followUp
        ? h("div", { className: "followup" },
            h("div", { className: "fq" }, followUp),
            h("textarea", { value: followAns, name: "frame-followup", onInput: function (e) { setFollowAns(e.target.value); } }))
        : null,
      h("div", { className: "row" },
        h("button", { className: "ghost", onClick: askFollowUp, disabled: busy }, "Ask a follow-up"),
        h("button", { className: "act", onClick: compile, disabled: busy }, "Compile & apply")
      ),
      h("div", { className: "status" }, status),
      compiled
        ? h("div", { className: "compiled" },
            isScene
              ? h("div", null, h("div", { className: "k" }, "Scene"), h("div", { className: "v" }, compiled.sceneFrame || "—"))
              : h("div", null,
                  h("div", { className: "k" }, "Summary"),   h("div", { className: "v" }, compiled.summary || "—"),
                  h("div", { className: "k" }, "Objective"), h("div", { className: "v" }, compiled.objective || "—"),
                  h("div", { className: "k" }, "Stance"),    h("div", { className: "v" }, compiled.stance || "—"))
          )
        : null
    );
  }

  // --- expression vocabulary (simple: label -> memory) ------------------
  function ExpressionStep(props) {
    return h("div", { className: "card" },
      h("h2", null, "How the Lamp Speaks"),
      h("p", { className: "lead" }, "The lamp answers only in light. Each behaviour is a name it can choose; you set what the light actually does, by hand, on the fixtures."),
      h("div", { className: "vocab" },
        props.expressions.map(function (e, i) {
          return h(Fragment, { key: i },
            h("div", { className: "lbl" }, h("input", { type: "text", value: e.actionLabel, readOnly: true, name: "expression-" + i })),
            h("div", { className: "mem" }, "→ " + e.memory)
          );
        })
      ),
      h("p", { className: "hint" }, "For the demo these behaviours are pre-authored. What each memory looks like in light is directed on the light controller — the seam between name and light stays where the scenographer wants it.")
    );
  }

  // --- stage: talk, direct, and watch the lamp explain itself -----------
  function Stage() {
    var stateState = useState({ dialogue: [], objective: "", stance: "" });
    var state = stateState[0], setState = stateState[1];
    var sayState = useState(""); var say = sayState[0], setSay = sayState[1];
    var directState = useState(""); var direct = directState[0], setDirect = directState[1];
    var feedRef = useRef(null);

    useEffect(function () {
      var live = true;
      function poll() {
        api("/api/state").then(function (s) { if (live && s) setState(s); });
      }
      poll();
      var id = setInterval(poll, 1500);
      return function () { live = false; clearInterval(id); };
    }, []);

    function sendSay() {
      var t = say.trim(); if (!t) return;
      setSay(""); api("/api/say", { text: t });
    }
    function sendDirect() {
      var t = direct.trim(); if (!t) return;
      setDirect(""); api("/api/direct", { text: t });
    }

    var dialogue = (state.dialogue || []).slice().reverse();  // newest first

    return h("div", null,
      h("div", { className: "card" },
        h("div", { className: "stage-bar" },
          h("input", {
            type: "text", placeholder: "Say something to the lamp…", value: say, name: "say-line",
            onInput: function (e) { setSay(e.target.value); },
            onKeyDown: function (e) { if (e.key === "Enter") sendSay(); }
          }),
          h("button", { className: "act", onClick: sendSay }, "Speak")
        ),
        h("div", { className: "stage-bar", style: { marginTop: "10px" } },
          h("input", {
            type: "text", placeholder: "Direct the lamp (e.g. \"keep them here\")…", value: direct, name: "direct-line",
            onInput: function (e) { setDirect(e.target.value); },
            onKeyDown: function (e) { if (e.key === "Enter") sendDirect(); }
          }),
          h("button", { className: "ghost", onClick: sendDirect }, "Direct")
        ),
        h("div", { className: "meta" },
          h("span", null, "Objective: ", h("b", null, state.objective || "—")),
          h("span", null, "Stance: ", h("b", null, state.stance || "—"))
        )
      ),
      h("div", { className: "card" },
        h("h2", null, "The Scene, Unfolding"),
        h("p", { className: "lead" }, "Everything the lamp does — and why it chose it."),
        h("div", { className: "feed", ref: feedRef },
          dialogue.length === 0
            ? h("div", { className: "empty" }, "The lamp waits, listening.")
            : dialogue.map(function (t, i) {
                return h("div", { className: "turn", key: dialogue.length - i },
                  t.actor ? h("div", { className: "said" }, h("span", { className: "who" }, "Actor"), t.actor) : null,
                  h("div", { className: "did" }, t.action || "—"),
                  h("div", { className: "why" }, t.why || "")
                );
              })
        )
      )
    );
  }

  // --- character chat (minimal, one question at a time) ----------------
  function CharacterChat(props) {
    var questions = props.questions || [];
    var idxState = useState(0); var idx = idxState[0], setIdx = idxState[1];
    var answersState = useState([]); var answers = answersState[0], setAnswers = answersState[1];
    var inputRef = useRef(null);
    var phaseState = useState("asking"); var phase = phaseState[0], setPhase = phaseState[1]; // asking|followup|thinking|done
    var followUpState = useState(""); var followUp = followUpState[0], setFollowUp = followUpState[1];
    var compiledState = useState(null); var compiled = compiledState[0], setCompiled = compiledState[1];

    function itemsFrom(ans) {
      return questions.map(function (q, i) { return { question: q.question, answer: ans[i] || "" }; });
    }
    function askFollowUpThenCompile(ans) {
      setPhase("thinking");
      api("/api/frame/followup", { type: "dramaturgy", items: itemsFrom(ans) }).then(function (r) {
        if (r && r.followUp) { setFollowUp(r.followUp); setPhase("followup"); }
        else compileNow(ans, "", "");
      });
    }
    function compileNow(ans, fq, fa) {
      setPhase("thinking");
      api("/api/frame/compile", { type: "dramaturgy", items: itemsFrom(ans), followUpQuestion: fq, followUpAnswer: fa })
        .then(function (r) { setCompiled(r || {}); setPhase("done"); });
    }
    function submit() {
      var val = (inputRef.current ? inputRef.current.value : "").trim(); if (!val) return;
      if (inputRef.current) inputRef.current.value = "";
      if (phase === "asking") {
        var na = answers.slice(); na[idx] = val; setAnswers(na);
        if (idx + 1 < questions.length) setIdx(idx + 1);
        else askFollowUpThenCompile(na);
      } else if (phase === "followup") {
        compileNow(answers, followUp, val);
      }
    }

    if (questions.length === 0)
      return h("div", { className: "card" }, h("div", { className: "empty" }, "No questions configured."));

    var current = phase === "asking" ? questions[idx].question : (phase === "followup" ? followUp : "");
    var history = [];
    for (var i = 0; i < answers.length; i++) {
      if (answers[i]) history.push({ q: questions[i].question, a: answers[i] });
    }

    return h("div", { className: "card" },
      h("h2", null, "Who the Lamp Is"),
      h("p", { className: "lead" }, "Answer as the lamp, one thing at a time."),
      phase === "asking" ? h("div", { className: "progress" }, (idx + 1) + " / " + questions.length) : null,
      h("div", { className: "chat-log" },
        history.map(function (turn, i) {
          return h("div", { className: "chat-pair", key: i },
            h("div", { className: "cq" }, turn.q),
            h("div", { className: "ca" }, turn.a));
        })
      ),
      (phase === "asking" || phase === "followup")
        ? h("div", null,
            h("div", { className: "followup" }, h("div", { className: "fq" }, current)),
            h("div", { className: "stage-bar", style: { marginTop: "12px" } },
              h("input", {
                type: "text", placeholder: "…", key: "chat-input", ref: inputRef, name: "chat-answer",
                onKeyDown: function (e) { if (e.key === "Enter") submit(); }
              }),
              h("button", { className: "act", onClick: submit }, phase === "followup" ? "Answer" : "Next"))
          )
        : null,
      phase === "thinking" ? h("div", { className: "status" }, "The lamp is gathering itself…") : null,
      (phase === "done" && compiled)
        ? h("div", null,
            h("div", { className: "compiled" },
              h("div", { className: "k" }, "Summary"),   h("div", { className: "v" }, compiled.summary || "—"),
              h("div", { className: "k" }, "Objective"), h("div", { className: "v" }, compiled.objective || "—"),
              h("div", { className: "k" }, "Stance"),    h("div", { className: "v" }, compiled.stance || "—")),
            h("div", { className: "row" },
              h("button", { className: "act", onClick: props.goToStage }, "Enter the scene")))
        : null
    );
  }

  // --- app shell --------------------------------------------------------
  function App() {
    var stepState = useState("stage"); var step = stepState[0], setStep = stepState[1];
    var dramaState = useState([]); var drama = dramaState[0], setDrama = dramaState[1];
    var sceneState = useState([]); var scene = sceneState[0], setScene = sceneState[1];
    var exprState = useState([]); var expr = exprState[0], setExpr = exprState[1];
    var readyState = useState(false); var ready = readyState[0], setReady = readyState[1];

    useEffect(function () {
      api("/api/frames").then(function (f) {
        if (f) {
          setDrama(f.dramaturgy || []);
          setScene(f.scene || []);
          setExpr(f.expressions || []);
        }
        setReady(true);
      });
    }, []);

    function resetVisitor() {
      api("/api/preset/load", {}).then(function () {
        return api("/api/frames");
      }).then(function (f) {
        if (f) { setDrama(f.dramaturgy || []); setScene(f.scene || []); setExpr(f.expressions || []); }
        setStep("dramaturgy");
      });
    }

    var body;
    if (!ready) body = h("div", { className: "empty" }, "Lighting the lamp…");
    else if (step === "dramaturgy") body = h(CharacterChat, { questions: drama, goToStage: function () { setStep("stage"); } });
    else if (step === "scene")      body = h(FrameStep, { kind: "scene", items: scene, setItems: setScene });
    else if (step === "expression") body = h(ExpressionStep, { expressions: expr });
    else                             body = h(Stage, null);

    return h("div", { className: "wrap" },
      h("div", { className: "brand" },
        h("div", { className: "lamp-dot" }),
        h("h1", null, "The Lamp"),
        h("p", null, "Author it, direct it, and it answers in light.")
      ),
      h("div", { className: "steps" },
        STEPS.map(function (s, i) {
          return h("button", {
            key: s.key,
            className: "step" + (step === s.key ? " active" : ""),
            onClick: function () { setStep(s.key); }
          }, h("span", { className: "n" }, i + 1), s.label);
        })
      ),
      body,
      h("div", { className: "footer" },
        h("button", { className: "ghost", onClick: resetVisitor }, "Reset the character")
      )
    );
  }

  preact.render(h(App, null), document.getElementById("root"));
})();
