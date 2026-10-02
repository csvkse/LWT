#define _GNU_SOURCE
#include <pty.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <signal.h>
#include <poll.h>
#include <sys/ioctl.h>
#include <sys/wait.h>

/* The forked child stays entirely in native code and never returns to the CLR. */
int lwt_pty_start(const char *exe, char *const argv[], char *const envp[], const char *cwd,
                  int columns, int rows, int *master) {
    int errors[2];
    if (pipe2(errors, O_CLOEXEC) < 0) return -errno;
    struct winsize size = { .ws_row = rows, .ws_col = columns };
    pid_t pid = forkpty(master, NULL, NULL, &size);
    if (pid == 0) {
        close(errors[0]);
        sigset_t signals;
        sigemptyset(&signals);
        sigprocmask(SIG_SETMASK, &signals, NULL);
        signal(SIGINT, SIG_DFL);
        signal(SIGQUIT, SIG_DFL);
        signal(SIGTERM, SIG_DFL);
        signal(SIGPIPE, SIG_DFL);
        if (cwd && chdir(cwd) < 0) {
            int error = errno;
            write(errors[1], &error, sizeof(error));
            _exit(127);
        }
        execve(exe, argv, envp);
        int error = errno;
        write(errors[1], &error, sizeof(error));
        _exit(127);
    }
    int error = errno;
    close(errors[1]);
    if (pid < 0) { close(errors[0]); return -error; }
    int child_error = 0;
    ssize_t count;
    do { count = read(errors[0], &child_error, sizeof(child_error)); } while (count < 0 && errno == EINTR);
    close(errors[0]);
    if (count > 0) {
        close(*master);
        waitpid(pid, NULL, 0);
        return -child_error;
    }
    fcntl(*master, F_SETFD, FD_CLOEXEC);
    fcntl(*master, F_SETFL, fcntl(*master, F_GETFL) | O_NONBLOCK);
    return pid;
}

int lwt_pty_read(int fd, unsigned char *buffer, int length) {
    struct pollfd descriptor = { .fd = fd, .events = POLLIN };
    int ready = poll(&descriptor, 1, 100);
    if (ready == 0 || (ready < 0 && errno == EINTR)) return -EAGAIN;
    if (ready < 0) return -errno;
    int count = read(fd, buffer, length);
    if (count < 0 && errno == EIO) return 0;
    return count < 0 ? -errno : count;
}

int lwt_pty_write(int fd, const unsigned char *buffer, int length) {
    int count = write(fd, buffer, length);
    return count < 0 ? -errno : count;
}

int lwt_pty_resize(int fd, int columns, int rows) {
    struct winsize size = { .ws_row = rows, .ws_col = columns };
    return ioctl(fd, TIOCSWINSZ, &size) < 0 ? -errno : 0;
}

int lwt_pty_foreground(int fd) { return tcgetpgrp(fd); }
int lwt_pty_wait(int pid, int *exit_code) {
    int status;
    int result = waitpid(pid, &status, WNOHANG);
    if (result == pid) {
        *exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
        return 1;
    }
    return result < 0 ? -errno : 0;
}
void lwt_pty_close(int fd) { close(fd); }
void lwt_pty_kill(int pid) { kill(-pid, SIGKILL); kill(pid, SIGKILL); }
