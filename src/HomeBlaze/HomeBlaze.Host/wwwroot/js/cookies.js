// A cookie and not local storage, so that the server already renders the page with the preference. Kept for a year.
export function setCookie(name, value) {
    document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=lax`;
}
