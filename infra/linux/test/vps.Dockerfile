# Test-only stand-in for a fresh Ubuntu 24.04 VPS: systemd as PID 1, sshd installed (as on every cloud image).
# Used by infra/linux/test/run.sh; never deployed anywhere. Units that make no sense (or hang) in a container are masked.
# Same digest as UBUNTU_IMAGE in .github/workflows/ci-linux.yml.
FROM ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852

ENV container=docker \
    DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      systemd systemd-sysv dbus ca-certificates curl openssh-server sudo iproute2 \
 && rm -rf /var/lib/apt/lists/* \
 && systemctl mask \
      getty.target console-getty.service systemd-logind.service systemd-udevd.service \
      systemd-udev-trigger.service systemd-firstboot.service systemd-networkd-wait-online.service \
 && systemctl set-default multi-user.target

STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
