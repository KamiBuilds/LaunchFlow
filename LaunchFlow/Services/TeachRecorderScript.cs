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
            // Only the element's structural location is recorded, never its value or text.
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
          // Record a small allowlist of non-text keyboard commands only.
          // Never read a field's value or record keystrokes from login, OTP,
          // payment, password or sensitive autocomplete forms.
          const safeKeys = new Set(['Enter','Tab','Escape','ArrowUp','ArrowDown','ArrowLeft','ArrowRight']);
          document.addEventListener('keydown', event => {
            if (!event.isTrusted || event.repeat || event.isComposing || event.ctrlKey ||
                event.altKey || event.metaKey || !sessions.length || !safeKeys.has(event.key)) return;
            const target = event.target;
            if (!(target instanceof Element)) return;
            if (target.closest('button,a,[role="button"],[role="link"]')) return; // Enter already creates a click.
            const form = target.closest('form');
            const input = target.closest('input,textarea,[contenteditable],[role="textbox"]');
            if (input) {
              const type = (input.getAttribute('type') || 'text').toLowerCase();
              const hint = (input.getAttribute('autocomplete') || '').toLowerCase();
              if (['password','email','tel','hidden','number'].includes(type) ||
                  /(password|username|email|one-time-code|cc-|otp|token)/.test(hint)) return;
            }
            if (form && form.querySelector('input[type="password"],input[type="email"],[autocomplete*="password"],[autocomplete="one-time-code"]')) return;
            for (const session of sessions)
              void window.__launchFlowTeachDispatchV1(JSON.stringify({
                type:'Press',id:session.id,epoch:session.epoch,target:event.key
              })).catch(() => {});
          }, true);

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
