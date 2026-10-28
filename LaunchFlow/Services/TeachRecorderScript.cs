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
            // Keyboard submit on a login/payment form can synthesize a trusted
            // button Click. Never record it as a safe post-login action.
            const ownerForm = ('form' in element ? element.form : null) || element.closest('form');
            if (ownerForm && (sensitive(ownerForm) || hasSensitiveFields(ownerForm))) {
              unsupported();
              return;
            }
            const selector = cssPath(element);
            for (const session of sessions) {
              void window.__launchFlowTeachDispatchV1(JSON.stringify({type:'Click',id:session.id,epoch:session.epoch,target:selector})).catch(() => {});
            }
          };
          // Keep it simple: record only named non-text keys on ordinary, top-level
          // native controls. Fail closed for shadow-root retargeting (including
          // closed roots), arbitrary custom elements and credential-bearing forms.
          const safeKeys = new Set(['Enter','Tab','Escape','ArrowUp','ArrowDown','ArrowLeft','ArrowRight']);
          const sensitiveHints = /(password|username|email|one-time-code|cc-|otp|token|webauthn|payment|card|address|name|tel)/i;
          const sensitive = input => {
            const kind = (input.getAttribute('type') || 'text').toLowerCase();
            const hint = input.getAttribute('autocomplete') || '';
            const name = input.getAttribute('name') || '';
            const id = input.getAttribute('id') || '';
            return ['password','email','tel','hidden','number'].includes(kind) ||
              sensitiveHints.test(hint) || sensitiveHints.test(name) || sensitiveHints.test(id);
          };
          const hasSensitiveFields = form =>
            form && [...form.elements].some(field =>
              field instanceof Element && (sensitive(field) ||
                field.matches('input[type="password"],input[type="email"],[autocomplete*="password"],[autocomplete="one-time-code"]')));
          const unsupported = () => {
            for (const session of sessions)
              void window.__launchFlowTeachDispatchV1(JSON.stringify({
                type:'Unsupported',id:session.id,epoch:session.epoch
              })).catch(() => {});
          };
          document.addEventListener('keydown', event => {
            if (!event.isTrusted || event.repeat || event.isComposing ||
                !sessions.length || !safeKeys.has(event.key)) return;
            // A modified key is not the unmodified command: never serialize
            // Shift+Tab as Tab. Show the unsupported warning instead.
            if (event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
              unsupported();
              return;
            }
            const target = event.target;
            if (!(target instanceof Element)) return;
            const path = event.composedPath();
            if (target.getRootNode() !== document ||
                path.some(node => node instanceof ShadowRoot) ||
                target.shadowRoot || target.localName.includes('-')) {
              unsupported();
              return;
            }
            // Closed shadow roots retarget to their host. Only known native
            // controls at the document root are eligible; unknown hosts are denied.
            if (!target.matches('button,a,select,summary,input,textarea,body')) {
              unsupported();
              return;
            }
            // Enter on buttons/links naturally emits a trusted click, which
            // the click recorder already captures. Other keys must not be lost.
            if (event.key === 'Enter' &&
                target.closest('button,a,[role="button"],[role="link"]')) return;
            const input = target.closest('input,textarea,[contenteditable],[role="textbox"]');
            // Form-associated buttons and inputs can live outside the form's subtree.
            // Resolve their actual owner before applying sensitive-form exclusions.
            const form = ('form' in target ? target.form : null) ||
              target.closest('form');
            if ((input && (sensitive(input) ||
                input.matches('[contenteditable],[role="textbox"]'))) ||
                (form && sensitive(form)) || hasSensitiveFields(form)) {
              unsupported();
              return;
            }
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
