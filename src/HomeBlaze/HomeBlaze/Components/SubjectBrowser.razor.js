export function scrollIntoView(elementId, left) {
    const element = document.getElementById(elementId);
    if (element) {
        element.scrollTo({ left: left, behavior: 'smooth' });
    }
}
