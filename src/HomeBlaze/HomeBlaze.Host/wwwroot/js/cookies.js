// A cookie and not local storage, so that the server already renders the page with the preference.
export function setCookie(name, value) {
    document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=lax`;
}
