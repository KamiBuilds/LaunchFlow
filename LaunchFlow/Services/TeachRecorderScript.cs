namespace LaunchFlow.Services;

internal static class TeachRecorderScript
{
    // Init scripts evaluate expressions, unlike EvaluateAsync they do not invoke an arrow function.
    internal const string Install = """
        (() => {
          if (window.__launchFlowTeachRecorderV1) return;
          let revision = -1;
          let sessions = [];
          const update = state => {
            if (!state || state.revision < revision) return;
            revision = state.revision;
            sessions = state.sessions;
          };
          window.__launchFlowTeachRecorderV1 = { update };
          const editable = 'input,textarea,select,[contenteditable],[role="textbox"],[role="combobox"]';
          const cssPath = element => {
            const parts = [];
            for (let el = element; el; el = el.parentElement) {
              let part = el.tagName.toLowerCase();
              if (el.parentElement) {
                const siblings = [...el.parentElement.children].filter(n => n.tagName === el.tagName);
                if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(el) + 1) + ')';
              }
              parts.unshift(part);
            }
            return parts.join(' > ');
          };
          const record = event => {
            if (!event.isTrusted || !sessions.length) return;
            // Do not read values, labels, IDs, or text: even button text can contain an echoed password.
            if (event.composedPath().some(el => el instanceof Element && el.closest(editable))) return;
            const target = event.target;
            if (!(target instanceof Element)) return;
            if (event.composedPath().some(node => node instanceof ShadowRoot)) {
              for (const session of sessions)
                void window.__launchFlowTeachDispatchV1(JSON.stringify({type:'Unsupported',id:session.id,epoch:session.epoch})).catch(() => {});
              return;
            }
            const element = target.closest('a,button,[role="button"],[role="link"],label') || target;
            const selector = cssPath(element);
            for (const session of sessions) {
              void window.__launchFlowTeachDispatchV1(JSON.stringify({type:'Click',id:session.id,epoch:session.epoch,target:selector})).catch(() => {});
            }
          };
          // A press is not a completed click: dragging away or replacing the target must not teach a phantom action.
          let pointerTarget = null;
          document.addEventListener('pointerdown', event => {
            pointerTarget = event.isTrusted && event.button === 0 && event.isPrimary ? event.target : null;
          }, true);
          document.addEventListener('pointercancel', () => { pointerTarget = null; }, true);
          document.addEventListener('click', event => {
            const completed = event.detail === 0 || event.target === pointerTarget;
            pointerTarget = null;
            if (completed) record(event);
          }, true);
          void window.__launchFlowTeachDispatchV1('{"type":"hello"}').then(json => update(JSON.parse(json))).catch(() => {});
        })()
        """;
}
