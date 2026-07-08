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
    { key: "stage",      label: "Stage" },
    { key: "tech",       label: "Technical" }
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
    var checkedState = useState(false); var checked = checkedState[0], setChecked = checkedState[1];
    var compiledState = useState(null); var compiled = compiledState[0], setCompiled = compiledState[1];
    var statusState = useState(""); var status = statusState[0], setStatus = statusState[1];
    var busyState = useState(false); var busy = busyState[0], setBusy = busyState[1];
    var reviseRef = useRef(null);

    function edit(i, val) {
      var next = items.slice();
      next[i] = { question: next[i].question, answer: val };
      setItems(next);
    }

    function reviseScene() {
      var val = (reviseRef.current ? reviseRef.current.value : "").trim(); if (!val) return;
      if (reviseRef.current) reviseRef.current.value = "";
      setBusy(true); setStatus("Adjusting…");
      api("/api/frame/revise-scene", { sceneFrame: (compiled && compiled.sceneFrame) || "", change: val })
        .then(function (r) { setCompiled(r || compiled); setStatus("Adjusted."); setBusy(false); });
    }

    function compileNow() {
      setBusy(true); setStatus("Confirming…");
      api("/api/frame/compile", {
        type: kind, items: items,
        followUpQuestion: followUp, followUpAnswer: followAns
      }).then(function (r) {
        setCompiled(r || {});
        setStatus("Confirmed. The lamp is playing this now.");
        setBusy(false);
      });
    }

    function confirm() {
      if (!checked) {
        // First Confirm: let the system decide whether it needs one clarification.
        setBusy(true); setStatus("The lamp is considering what it still needs to know…");
        api("/api/frame/followup", { type: kind, items: items }).then(function (r) {
          setChecked(true);
          if (r && r.followUp) { setFollowUp(r.followUp); setStatus(""); setBusy(false); }
          else { compileNow(); }
        });
      } else {
        compileNow();
      }
    }

    function enrich() {
      setBusy(true); setStatus("Enriching…");
      api("/api/frame/enrich", { type: kind, sceneFrame: (compiled && compiled.sceneFrame) || "" })
        .then(function (r) { setCompiled(r || compiled); setStatus("Enriched."); setBusy(false); });
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
        h("button", { className: "act", onClick: confirm, disabled: busy }, "Confirm")
      ),
      h("div", { className: "status" }, status),
      compiled
        ? h("div", null,
            h("div", { className: "compiled" },
              isScene
                ? h("div", null, h("div", { className: "k" }, "Scene"), h("div", { className: "v" }, compiled.sceneFrame || "—"))
                : h("div", null,
                    h("div", { className: "k" }, "Summary"),   h("div", { className: "v" }, compiled.summary || "—"),
                    h("div", { className: "k" }, "Objective"), h("div", { className: "v" }, compiled.objective || "—"),
                    h("div", { className: "k" }, "Stance"),    h("div", { className: "v" }, compiled.stance || "—"))),
            isScene
              ? h("div", null,
                  h("div", { className: "followup", style: { marginTop: "14px" } },
                    h("div", { className: "fq" }, "Want to change something about the scene?")),
                  h("div", { className: "stage-bar", style: { marginTop: "10px" } },
                    h("input", { type: "text", placeholder: "e.g. make it night; add a second person…",
                      key: "scene-revise", ref: reviseRef, name: "scene-revise",
                      onKeyDown: function (e) { if (e.key === "Enter") reviseScene(); } }),
                    h("button", { className: "ghost", onClick: reviseScene, disabled: busy }, "Adjust")))
              : null,
            h("div", { className: "row", style: { marginTop: "14px" } },
              h("button", { className: "ghost", onClick: enrich, disabled: busy }, "Enrich with imagination"),
              (isScene && props.onNext)
                ? h("button", { className: "act", onClick: props.onNext }, "Continue to Expression")
                : null))
        : null
    );
  }

  // --- expression vocabulary: label + light (colour/brightness/pulse) + sound
  function ExpressionStep() {
    var listState = useState([]); var behaviors = listState[0], setBehaviors = listState[1];
    var statusState = useState(""); var status = statusState[0], setStatus = statusState[1];
    var busyState = useState(false); var busy = busyState[0], setBusy = busyState[1];
    var micsState = useState([]); var mics = micsState[0], setMics = micsState[1];
    var micState = useState(""); var mic = micState[0], setMic = micState[1];
    var modeState = useState("keep"); var mode = modeState[0], setMode = modeState[1];
    var holdState = useState(4); var holdSecs = holdState[0], setHoldSecs = holdState[1];
    var nColorState = useState("#FFB45A"); var nColor = nColorState[0], setNColor = nColorState[1];
    var nBriState = useState(0.12); var nBri = nBriState[0], setNBri = nBriState[1];
    var fileRef = useRef(null);
    var fileIndexRef = useRef(0);

    function refresh() {
      api("/api/expression/list").then(function (r) {
        // Don't wipe local edits if the server hands back nothing (e.g. Actuator not assigned).
        if (r && r.behaviors && r.behaviors.length) setBehaviors(r.behaviors);
        if (r && r.mode) {
          setMode(r.mode);
          if (typeof r.holdSeconds === "number") setHoldSecs(r.holdSeconds);
          if (r.neutralColorHex) setNColor(r.neutralColorHex);
          if (typeof r.neutralBrightness === "number") setNBri(r.neutralBrightness);
        }
      });
    }
    useEffect(function () {
      refresh();
      api("/api/expression/mics").then(function (r) { if (r && r.mics) setMics(r.mics); });
    }, []);

    function edit(i, field, value) {
      var next = behaviors.slice();
      var row = Object.assign({}, next[i]); row[field] = value; next[i] = row;
      setBehaviors(next);
    }

    function applyAll() {
      setBusy(true); setStatus("Applying…");
      api("/api/expression/apply", {
        behaviors: behaviors,
        mode: mode, holdSeconds: holdSecs, neutralColorHex: nColor, neutralBrightness: nBri
      }).then(function (r) {
        if (r && r.behaviors && r.behaviors.length) {
          setBehaviors(r.behaviors);
          setStatus("Applied. The lamp can use these now.");
        } else {
          // Keep the user's rows; the server had nowhere to store them.
          setStatus("Couldn't save — assign the CNCLampActuator to the bridge's Actuator slot, then re-enter Play.");
        }
        setBusy(false);
      });
    }

    function test(i) {
      api("/api/expression/apply", { behaviors: behaviors }).then(function () {
        return api("/api/expression/test", { index: i });
      });
    }

    function suggest() {
      setBusy(true); setStatus("Thinking of one more behavior…");
      api("/api/expression/suggest", {}).then(function (r) {
        var label = (r && r.behavior) || "";
        if (label) {
          var next = behaviors.slice();
          var slot = -1;
          for (var i = 0; i < next.length; i++) { if (!(next[i].label || "").trim()) { slot = i; break; } }
          if (slot < 0) { next.push({ label: "", colorHex: "#FFFFFF", brightness: 0.6, pulse: false, pulseCount: 0, pulseSeconds: 2, hasSound: false, soundFile: "" }); slot = next.length - 1; }
          var row = Object.assign({}, next[slot]); row.label = label; next[slot] = row;
          setBehaviors(next);
          setStatus("Suggested: “" + label + "” — now give it a light and a sound.");
        } else setStatus("No suggestion came back.");
        setBusy(false);
      });
    }

    function addRow() {
      setBehaviors(behaviors.concat([{ label: "", colorHex: "#FFFFFF", brightness: 0.6, pulse: false, pulseCount: 0, pulseSeconds: 2, hasSound: false, soundFile: "" }]));
    }

    function record(i) {
      // Apply edits first so the refresh afterwards can't wipe unsaved changes.
      setBusy(true); setStatus("Recording 3 seconds — make the sound now…");
      api("/api/expression/apply", { behaviors: behaviors }).then(function () {
        return api("/api/expression/record", { index: i, seconds: 3, mic: mic });
      }).then(function (r) {
        setStatus("Recording: " + ((r && r.status) || "?")); setBusy(false); refresh();
      });
    }

    function pickFile(i) {
      fileIndexRef.current = i;
      if (fileRef.current) fileRef.current.click();
    }

    function onFile(e) {
      var f = e.target.files && e.target.files[0];
      e.target.value = "";
      if (!f) return;
      var ext = (f.name.split(".").pop() || "wav").toLowerCase();
      setBusy(true); setStatus("Loading " + f.name + "…");
      api("/api/expression/apply", { behaviors: behaviors }).then(function () {
        return fetch("/api/expression/sound-upload?index=" + fileIndexRef.current + "&ext=" + ext, { method: "POST", body: f });
      }).then(function (r) { return r.json().catch(function () { return {}; }); })
        .then(function (r) { setStatus("Sound: " + ((r && r.status) || "?")); setBusy(false); refresh(); });
    }

    return h("div", { className: "card" },
      h("h2", null, "How the Lamp Speaks"),
      h("p", { className: "lead" }, "Each behavior is something the lamp can do: a name, a light, and — if you like — a sound. “I say yes” and “I say no” are its core; invent the rest."),
      h("input", { type: "file", accept: "audio/*", style: { display: "none" }, ref: fileRef, onChange: onFile, name: "sound-file" }),
      mics.length > 0
        ? h("div", { className: "mic-row" },
            h("span", null, "recording microphone:"),
            h("select", { value: mic, name: "mic-select", onChange: function (e) { setMic(e.target.value); } },
              h("option", { value: "" }, "system default"),
              mics.map(function (m) { return h("option", { value: m, key: m }, m); })))
        : null,
      h("div", { className: "exp-head" },
        h("span", null, "behavior"), h("span", null, "light"), h("span", null, "brightness"), h("span", null, "pulse"), h("span", null, "sound"), h("span", null, "")),
      behaviors.map(function (b, i) {
        return h("div", { className: "exp-row", key: i },
          h("input", { type: "text", value: b.label || "", placeholder: "i …", name: "beh-label-" + i,
            onInput: function (e) { edit(i, "label", e.target.value); } }),
          h("input", { type: "color", value: b.colorHex || "#FFC073", name: "beh-color-" + i,
            onInput: function (e) { edit(i, "colorHex", e.target.value); } }),
          h("input", { type: "range", min: 0, max: 1, step: 0.05, value: b.brightness, name: "beh-bri-" + i,
            onInput: function (e) { edit(i, "brightness", parseFloat(e.target.value)); } }),
          h("div", { className: "pulse-cell" },
            h("input", { type: "checkbox", checked: !!b.pulse, name: "beh-pulse-" + i,
              onChange: function (e) { edit(i, "pulse", e.target.checked); } }),
            b.pulse ? h(Fragment, null,
              h("input", { type: "number", min: 0, max: 30, step: 1, value: b.pulseCount || 0, className: "pulse-secs", name: "beh-pcount-" + i,
                title: "how many pulses (0 = keep pulsing)",
                onInput: function (e) { edit(i, "pulseCount", parseInt(e.target.value, 10) || 0); } }),
              h("span", { className: "pulse-sep" }, "× over"),
              h("input", { type: "number", min: 0.5, max: 30, step: 0.5, value: b.pulseSeconds, className: "pulse-secs", name: "beh-psec-" + i,
                title: "total seconds",
                onInput: function (e) { edit(i, "pulseSeconds", parseFloat(e.target.value) || 2); } }),
              h("span", { className: "pulse-sep" }, "s")) : null),
          h("div", { className: "sound-cell" },
            h("span", { className: "sound-dot" + (b.hasSound ? " on" : "") }),
            h("button", { className: "mini", onClick: function () { pickFile(i); }, disabled: busy }, "Load"),
            h("button", { className: "mini", onClick: function () { record(i); }, disabled: busy }, "Rec")),
          h("button", { className: "mini", onClick: function () { test(i); }, }, "Try"));
      }),
      h("div", { className: "mode-box" },
        h("div", { className: "mode-title" }, "After each response"),
        h("div", { className: "mode-row" },
          h("button", { className: "mode-btn" + (mode === "keep" ? " active" : ""), onClick: function () { setMode("keep"); } }, "Choose & keep"),
          h("button", { className: "mode-btn" + (mode === "return" ? " active" : ""), onClick: function () { setMode("return"); } }, "Act & return to neutral")),
        mode === "return"
          ? h("div", { className: "mode-row", style: { marginTop: "10px" } },
              h("span", { className: "pulse-sep" }, "hold steady responses for"),
              h("input", { type: "number", min: 0.5, max: 60, step: 0.5, value: holdSecs, className: "pulse-secs", name: "hold-secs",
                onInput: function (e) { setHoldSecs(parseFloat(e.target.value) || 4); } }),
              h("span", { className: "pulse-sep" }, "s, then return to neutral:"),
              h("input", { type: "color", value: nColor, name: "neutral-color",
                onInput: function (e) { setNColor(e.target.value); } }),
              h("input", { type: "range", min: 0, max: 1, step: 0.05, value: nBri, name: "neutral-bri", style: { width: "110px" },
                title: "neutral brightness",
                onInput: function (e) { setNBri(parseFloat(e.target.value)); } }))
          : h("p", { className: "hint", style: { marginTop: "8px" } },
              "Each response stays until the next one. A finite pulse settles at its full colour when done."),
        mode === "return"
          ? h("p", { className: "hint", style: { marginTop: "8px" } },
              "Pulsing responses return to neutral after their last pulse; steady ones after the hold time.")
          : null),
      h("div", { className: "row", style: { marginTop: "16px" } },
        h("button", { className: "ghost", onClick: suggest, disabled: busy }, "Suggest one more behavior"),
        h("button", { className: "ghost", onClick: addRow, disabled: busy }, "+ Add"),
        h("button", { className: "act", onClick: applyAll, disabled: busy }, "Apply")),
      h("div", { className: "status" }, status),
      h("p", { className: "hint" }, "“Try” plays the behavior on the lamp so you can judge it. The light and sound you design here is what the lamp will use when it chooses this behavior in the scene.")
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
        ),
        h("div", { className: "status", title: "What the performer is doing (or why the last turn failed)" },
          state.performerStatus || "")
      ),
      h("div", { className: "card carry" },
        h("div", { className: "carry-row" },
          h("span", { className: "carry-k" }, "Character"),
          h("span", { className: "carry-v" }, state.summary || "—")),
        h("div", { className: "carry-row" },
          h("span", { className: "carry-k" }, "Scene"),
          h("span", { className: "carry-v" }, state.sceneFrame || "—")),
        h("div", { className: "carry-row" },
          h("span", { className: "carry-k" }, "Can do"),
          (state.behaviorLabels && state.behaviorLabels.length)
            ? h("span", { className: "chips" },
                state.behaviorLabels.map(function (l, i) { return h("span", { className: "chip", key: i }, l); }))
            : h("span", { className: "carry-v warn" }, "no behaviors — set them in Expression and press Apply"))
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
    var reviseRef = useRef(null);
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
    function revise() {
      var val = (reviseRef.current ? reviseRef.current.value : "").trim(); if (!val) return;
      if (reviseRef.current) reviseRef.current.value = "";
      var cur = compiled || {};
      setPhase("thinking");
      api("/api/frame/revise", {
        summary: cur.summary || "", objective: cur.objective || "",
        obstacle: cur.obstacle || "", stance: cur.stance || "", change: val
      }).then(function (r) { setCompiled(r || cur); setPhase("done"); });
    }
    function enrich() {
      var cur = compiled || {};
      setPhase("thinking");
      api("/api/frame/enrich", {
        type: "dramaturgy", summary: cur.summary || "", objective: cur.objective || "",
        obstacle: cur.obstacle || "", stance: cur.stance || ""
      }).then(function (r) { setCompiled(r || cur); setPhase("done"); });
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
              h("div", { className: "k" }, "Obstacle"),  h("div", { className: "v" }, compiled.obstacle || "—"),
              h("div", { className: "k" }, "Stance"),    h("div", { className: "v" }, compiled.stance || "—")),
            h("div", { className: "followup", style: { marginTop: "14px" } },
              h("div", { className: "fq" }, "Want to change something about the character?")),
            h("div", { className: "stage-bar", style: { marginTop: "10px" } },
              h("input", {
                type: "text", placeholder: "e.g. make it more forgiving; give it a new obstacle…",
                key: "revise-input", ref: reviseRef, name: "revise",
                onKeyDown: function (e) { if (e.key === "Enter") revise(); }
              }),
              h("button", { className: "ghost", onClick: revise }, "Adjust")),
            h("div", { className: "row", style: { marginTop: "14px" } },
              h("button", { className: "ghost", onClick: enrich }, "Enrich with imagination"),
              h("button", { className: "act", onClick: props.goToStage }, "Continue to Scene")))
        : null
    );
  }

  // --- technical: model choice + the malleable coaching prompts ---------
  var TECH_PROMPTS = [
    { k: "performer",     label: "Acting — how the lamp performs each turn" },
    { k: "charFollowup",  label: "Character — when and how to ask a follow-up" },
    { k: "charCompile",   label: "Character — how the answers are compiled" },
    { k: "charEnrich",    label: "Character — how enrichment works" },
    { k: "charRevise",    label: "Character — how an adjustment is applied" },
    { k: "sceneFollowup", label: "Scene — when and how to ask a follow-up" },
    { k: "sceneCompile",  label: "Scene — how the answers are compiled" },
    { k: "sceneEnrich",   label: "Scene — how enrichment works" },
    { k: "sceneRevise",   label: "Scene — how an adjustment is applied" },
    { k: "suggest",       label: "Expression — how a behavior is suggested" }
  ];

  function TechStep() {
    var techState = useState(null); var tech = techState[0], setTech = techState[1];
    var statusState = useState(""); var status = statusState[0], setStatus = statusState[1];
    var busyState = useState(false); var busy = busyState[0], setBusy = busyState[1];
    var keyStatusState = useState(""); var keyStatus = keyStatusState[0], setKeyStatus = keyStatusState[1];
    var keyRef = useRef(null);

    useEffect(function () {
      api("/api/tech").then(function (r) { if (r) { if (r.prompts) setTech(r); setKeyStatus(r.keyStatus || ""); } });
    }, []);

    function saveKey() {
      var k = (keyRef.current ? keyRef.current.value : "").trim();
      if (!k) { setStatus("Paste a key first."); return; }
      if (keyRef.current) keyRef.current.value = "";
      setBusy(true); setStatus("Saving key…");
      api("/api/tech/key", { key: k }).then(function (r) {
        setKeyStatus((r && r.keyStatus) || "");
        setStatus((r && r.status) || "Key saved."); setBusy(false);
      });
    }

    function editPrompt(k, v) {
      var next = Object.assign({}, tech);
      next.prompts = Object.assign({}, tech.prompts);
      next.prompts[k] = v;
      setTech(next);
    }

    function payload() {
      var p = (tech && tech.prompts) || {};
      var out = { model: (tech && tech.model) || "" };
      TECH_PROMPTS.forEach(function (row) { out[row.k] = p[row.k] || ""; });
      return out;
    }

    function apply() {
      setBusy(true); setStatus("Applying…");
      api("/api/tech/apply", payload()).then(function (r) {
        if (r && r.prompts) setTech(r);
        setStatus("Applied."); setBusy(false);
      });
    }

    function resetAll() {
      setBusy(true); setStatus("Resetting to defaults…");
      api("/api/tech/reset", {}).then(function (r) {
        if (r && r.prompts) setTech(r);
        setStatus("Back to defaults."); setBusy(false);
      });
    }

    if (!tech) return h("div", { className: "card" }, h("div", { className: "empty" }, "Reading the machinery…"));

    return h("div", { className: "card" },
      h("h2", null, "Under the Hood"),
      h("p", { className: "lead" }, "The model, the key, and the coaching each model call receives. The parts that keep the machinery running (the JSON the system parses) are fixed and appended automatically — everything here is safe to rewrite."),
      h("div", { className: "mode-box" },
        h("div", { className: "mode-title" }, "OpenAI API key"),
        h("div", { className: "mode-row" },
          h("input", {
            type: "password", placeholder: "sk-…  (stored outside the project, never committed)",
            ref: keyRef, name: "openai-key", style: { flex: "1", minWidth: "260px" },
            onKeyDown: function (e) { if (e.key === "Enter") saveKey(); }
          }),
          h("button", { className: "act", onClick: saveKey, disabled: busy }, "Save key")),
        h("p", { className: "hint", style: { marginTop: "8px" } },
          (keyStatus && keyStatus.indexOf("not set") < 0)
            ? "Current: " + keyStatus + ". Saved to a local file outside git; paste a new key to replace it."
            : "No key set — the lamp can't reach OpenAI until you save one. It's stored locally, never in the scene or git.")),
      h("div", { className: "mode-box", style: { marginTop: "16px" } },
        h("div", { className: "mode-title" }, "Model — used for everything"),
        h("div", { className: "mode-row" },
          h("button", { className: "mode-btn" + (tech.model === "light" ? " active" : ""), onClick: function () { setTech(Object.assign({}, tech, { model: "light" })); } }, "Lightweight — GPT-4o mini"),
          h("button", { className: "mode-btn" + (tech.model === "heavy" ? " active" : ""), onClick: function () { setTech(Object.assign({}, tech, { model: "heavy" })); } }, "Heavyweight — GPT-5.4"))),
      TECH_PROMPTS.map(function (row) {
        return h("div", { className: "q", key: row.k, style: { marginTop: "18px" } },
          h("label", null, row.label),
          h("textarea", {
            value: (tech.prompts && tech.prompts[row.k]) || "", rows: 4, name: "tech-" + row.k,
            onInput: function (e) { editPrompt(row.k, e.target.value); }
          }));
      }),
      h("div", { className: "row", style: { marginTop: "16px" } },
        h("button", { className: "ghost", onClick: resetAll, disabled: busy }, "Reset to defaults"),
        h("button", { className: "act", onClick: apply, disabled: busy }, "Apply")),
      h("div", { className: "status" }, status)
    );
  }

  // --- app shell --------------------------------------------------------
  function App() {
    var stepState = useState("stage"); var step = stepState[0], setStep = stepState[1];
    var dramaState = useState([]); var drama = dramaState[0], setDrama = dramaState[1];
    var sceneState = useState([]); var scene = sceneState[0], setScene = sceneState[1];
    var exprState = useState([]); var expr = exprState[0], setExpr = exprState[1];
    var readyState = useState(false); var ready = readyState[0], setReady = readyState[1];
    var resetState = useState(0); var resetCount = resetState[0], setResetCount = resetState[1];
    var appMsgState = useState(""); var appMsg = appMsgState[0], setAppMsg = appMsgState[1];

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
        setResetCount(resetCount + 1); // remount all tabs with fresh state
        setStep("dramaturgy");
      });
    }

    function saveSetup() {
      setAppMsg("Saving…");
      api("/api/session/save", {}).then(function (r) { setAppMsg((r && r.status) || "Saved."); });
    }
    function loadSetup() {
      setAppMsg("Loading…");
      api("/api/session/load", {}).then(function (r) {
        if (r && r.loaded) location.reload();
        else setAppMsg("No saved setup found yet.");
      });
    }

    // All tabs stay mounted (hidden with display:none) so switching tabs never
    // loses in-progress answers or edits. resetCount keys force a true remount
    // only when "Reset the character" is pressed.
    var body;
    if (!ready) body = h("div", { className: "empty" }, "Lighting the lamp…");
    else body = h(Fragment, null,
      h("div", { style: { display: step === "dramaturgy" ? "" : "none" } },
        h(CharacterChat, { key: "chat" + resetCount, questions: drama, goToStage: function () { setStep("scene"); } })),
      h("div", { style: { display: step === "scene" ? "" : "none" } },
        h(FrameStep, { key: "scene" + resetCount, kind: "scene", items: scene, setItems: setScene, onNext: function () { setStep("expression"); } })),
      h("div", { style: { display: step === "expression" ? "" : "none" } },
        h(ExpressionStep, { key: "expr" + resetCount })),
      h("div", { style: { display: step === "stage" ? "" : "none" } },
        h(Stage, { key: "stage" + resetCount })),
      h("div", { style: { display: step === "tech" ? "" : "none" } },
        h(TechStep, { key: "tech" + resetCount })));

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
        appMsg ? h("span", { className: "app-msg" }, appMsg) : null,
        h("button", { className: "ghost", onClick: saveSetup }, "Save setup"),
        h("button", { className: "ghost", onClick: loadSetup }, "Load setup"),
        h("button", { className: "ghost", onClick: resetVisitor }, "Reset the character")
      )
    );
  }

  preact.render(h(App, null), document.getElementById("root"));
})();
