export async function copyInvite(url) {
    await navigator.clipboard.writeText(url);
}
