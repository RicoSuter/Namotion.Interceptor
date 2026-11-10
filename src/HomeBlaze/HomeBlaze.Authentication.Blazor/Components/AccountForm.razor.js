// Posts the form of an account dialog with fetch, so that the password goes to the endpoint, which sets the session
// cookie, and never through the circuit. A redirect loads the page anew; refusals are passed to the dialog to show.
const genericError = 'Something went wrong. Try again.';
const tooManyAttempts = 'Too many attempts. Try again later.';

export function register(form, dotNetReference) {
    form.addEventListener('submit', event => {
        event.preventDefault();
        if (form.dataset.submitting === 'true') {
            return;
        }

        form.dataset.submitting = 'true';
        submit(form)
            .then(messages => {
                if (messages !== null) {
                    return dotNetReference.invokeMethodAsync('OnRefused', messages);
                }
            })
            .catch(() => {
                // The dialog is gone.
            })
            .finally(() => {
                delete form.dataset.submitting;
            });
    });
}

// Returns the messages of a refusal, or null when the page is loading the redirect.
async function submit(form) {
    let response;
    try {
        response = await fetch(form.action, {
            method: 'POST',
            body: new FormData(form),
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json' }
        });
    } catch {
        return [genericError];
    } finally {
        clearPasswords(form);
    }

    if (response.status === 429) {
        return [tooManyAttempts];
    }

    let answer = null;
    try {
        answer = await response.json();
    } catch {
        // Not JSON, such as the answer of a proxy.
    }

    if (answer !== null && typeof answer.redirect === 'string' && isSameOrigin(answer.redirect)) {
        location.assign(answer.redirect);
        return null;
    }

    if (answer !== null && typeof answer.error === 'string') {
        return [answer.error];
    }

    if (answer !== null && Array.isArray(answer.errors)) {
        const errors = answer.errors.filter(error => typeof error === 'string');
        if (errors.length > 0) {
            return errors;
        }
    }

    return [genericError];
}

function clearPasswords(form) {
    for (const input of form.querySelectorAll('input[type="password"]')) {
        input.value = '';
    }
}

function isSameOrigin(url) {
    try {
        return new URL(url, location.href).origin === location.origin;
    } catch {
        return false;
    }
}
