// Keep the .app executable directory limited to native code while preserving the
// ordinary self-contained .NET publish layout under Contents/Resources/app.
#include <mach-o/dyld.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv) {
    (void)argc;
    uint32_t size = 0;
    _NSGetExecutablePath(NULL, &size);
    char *raw = malloc(size);
    char executable[PATH_MAX];
    if (!raw || _NSGetExecutablePath(raw, &size) || !realpath(raw, executable)) {
        fprintf(stderr, "Cannot locate the application executable\n");
        free(raw);
        return 127;
    }
    free(raw);
    char *name = strrchr(executable, '/');
    if (!name) return 127;
    *name++ = '\0';
    char target[PATH_MAX];
    if (snprintf(target, sizeof(target), "%s/../Resources/app/%s", executable, name) >= sizeof(target)) {
        fprintf(stderr, "Application path is too long\n");
        return 127;
    }
    argv[0] = target;
    execv(target, argv);
    fprintf(stderr, "Cannot launch %s: %s\n", target, strerror(errno));
    return 127;
}
