/* ****************************************************************************
*
* Copyright (c) Microsoft Corporation. All rights reserved.
*
*
* This file is part of Microsoft R Host.
*
* Microsoft R Host is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 2 of the License, or
* (at your option) any later version.
*
* Microsoft R Host is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with Microsoft R Host.  If not, see <http://www.gnu.org/licenses/>.
*
* ***************************************************************************/

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string>
#include <mutex>
#include <sys/types.h>
#include <sys/wait.h>
#include <pwd.h>
#include <grp.h>
#include <unistd.h>
#include <security/pam_appl.h>
#include <security/pam_misc.h>
#include <boost/endian/buffers.hpp>
#include <libexplain/execv.h>

#include "picojson.h"
#include "util.h"

FILE *input = nullptr, *output = nullptr, *error = nullptr;

static constexpr int RTVS_AUTH_OK           = 0;
static constexpr int  RTVS_AUTH_INIT_FAILED = 200;
static constexpr int  RTVS_AUTH_BAD_INPUT   = 201;
static constexpr int  RTVS_AUTH_NO_INPUT    = 202;

static constexpr char RTVS_JSON_MSG_NAME[] = "name";
static constexpr char  RTVS_JSON_MSG_USERNAME[] = "username";
static constexpr char  RTVS_JSON_MSG_PASSWORD[] = "password";
static constexpr char  RTVS_JSON_MSG_ARGS[] = "arguments";
static constexpr char  RTVS_JSON_MSG_ENV[] = "environment";


static constexpr char  RTVS_RESPONSE_TYPE_PAM_INFO[] = "pam-info";
static constexpr char  RTVS_RESPONSE_TYPE_PAM_ERROR[] = "pam-error";
static constexpr char  RTVS_RESPONSE_TYPE_SYSTEM_ERROR[] = "unix-error";
static constexpr char  RTVS_RESPONSE_TYPE_JSON_ERROR[] = "json-error";
static constexpr char  RTVS_RESPONSE_TYPE_RTVS_RESULT[] = "rtvs-result";
static constexpr char  RTVS_RESPONSE_TYPE_RTVS_ERROR[] = "rtvs-error";

static constexpr char RTVS_MSG_AUTH_ONLY[] = "AuthOnly";
static constexpr char RTVS_MSG_AUTH_AND_RUN[] = "AuthAndRun";

static constexpr char RTVS_RHOST_PATH[] = "/usr/bin/Microsoft.R.Host";

std::string read_string(FILE* stream) {
    boost::endian::little_uint32_buf_t data_size;
    if (fread(&data_size, sizeof data_size, 1, stream) != 1) {
        return std::string();
    }

    std::string str(data_size.value(), '\0');
    if (!str.empty()) {
        if (fread(&str[0], str.size(), 1, stream) != 1) {
            return std::string();
        }
    }

    return str;
}

void write_string(FILE* stream, const std::string &data) {
    boost::endian::little_uint32_buf_t data_size(static_cast<uint32_t>(data.size()));

    if (fwrite(&data_size, sizeof data_size, 1, stream) == 1) {
        if (fwrite(data.data(), data.size(), 1, stream) == 1) {
            fflush(stream);
        } else {
            std::terminate();
        }
    } else {
        std::terminate();
    }
}

template<class Arg, class... Args>
inline void write_json(Arg&& arg, Args&&... args) {
    picojson::array msg;
    msg.push_back(picojson::value(std::forward<Arg>(arg)));
    append_json(msg, std::forward<Args>(args)...);
    write_string(output, picojson::value(msg).serialize());
}

int rtvs_conv(int num_msg, const pam_message **msgm, pam_response **response, void *appdata_ptr) {
    if (num_msg < 0) {
        return PAM_CONV_ERR;
    }

    pam_response *reply = (pam_response*) calloc(num_msg, sizeof(pam_response));
    if (reply == nullptr) {
        return PAM_CONV_ERR;
    }

    for (int count = 0; count < num_msg; ++count) {
        char *str = nullptr;
        switch (msgm[count]->msg_style) {
        case PAM_PROMPT_ECHO_OFF:
            str = strdup((char*)appdata_ptr);
            break;
        case PAM_PROMPT_ECHO_ON:
            str = strdup((char*)appdata_ptr);
            break;
        case PAM_ERROR_MSG:
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, msgm[count]->msg);
            break;
        case PAM_TEXT_INFO:
            write_json(RTVS_RESPONSE_TYPE_PAM_INFO, msgm[count]->msg);
            break;
        }

        if (str) {
            reply[count].resp_retcode = 0;
            reply[count].resp = str;
            str = nullptr;
        }
    }

    *response = reply;
    reply = nullptr;

    return PAM_SUCCESS;
}

int rtvs_conv_quiet(int num_msg, const pam_message **msgm, pam_response **response, void *appdata_ptr) {
    if (num_msg < 0) {
        return PAM_CONV_ERR;
    }

    pam_response *reply = (pam_response*)calloc(num_msg, sizeof(pam_response));
    if (reply == nullptr) {
        return PAM_CONV_ERR;
    }

    for (int count = 0; count < num_msg; ++count) {
        char *str = nullptr;
        switch (msgm[count]->msg_style) {
        case PAM_PROMPT_ECHO_OFF:
            str = strdup((char*)appdata_ptr);
            break;
        case PAM_PROMPT_ECHO_ON:
            str = strdup((char*)appdata_ptr);
            break;
        case PAM_ERROR_MSG:
        case PAM_TEXT_INFO:
            break;
        }

        if (str) {
            reply[count].resp_retcode = 0;
            reply[count].resp = str;
            str = nullptr;
        }
    }

    *response = reply;
    reply = nullptr;

    return PAM_SUCCESS;
}

std::string get_user_home(const std::string &username) {
    struct passwd *pw = getpwnam(username.c_str());
    if (pw && pw->pw_dir && pw->pw_dir[0] != '\0') {
        return std::string(strdup(pw->pw_dir));
    }
    return std::string();
}

template<typename T>
T calloc_or_exit(size_t count, size_t size) {
    T v = (T)calloc(count, size);
    if (!v) {
        // TODO: log error
        _exit(EXIT_FAILURE);
    }
}

void start_rhost(const picojson::object& json) {
    // construct arguments
    picojson::array json_args = json[RTVS_JSON_MSG_ARGS].get<picojson::array>();

    // <binary path> <arg 1> <arg 2> ... <explicit null>
    int argc = json_args.size() + 2;
    char **argv = calloc_or_exit<char**>(argc, sizeof *argv);

    // first item in the args must always be path to the binary
    std::string pathname(RTVS_RHOST_PATH);
    argv[0] = calloc_or_exit<char*>(pathname.length() + 1, sizeof **argv);
    memcpy(argv[0], pathname.c_str(), pathname.size());

    for (int i = 1; i < (argc - 1); ++i) {
        std::string item(json_args[i - 1].get<std::string>());
        argv[i] = calloc_or_exit<char*>(item.length() + 1, sizeof **argv);
        memcpy(argv[i], item.c_str(), item.size());
    }

    // explicit null for the end of arguments
    argv[argc - 1] = NULL;

    // construct environment
    picojson::array json_env = json[RTVS_JSON_MSG_ENV].get<picojson::array>();

    // <key1=value1> <key2=value2> ... <explicit null>
    int envc = json_env.size() + 1;
    char **envp = calloc_or_exit<char**>(envc, sizeof *envp);

    for (int i = 0; i < (argc - 1); ++i) {
        std::string env(json_env[i].get<std::string>());
        envp[i] = calloc_or_exit<char*>(env.length() + 1, sizeof **envp);
        memcpy(envp[i], env.c_str(), env.size());
    }

    // explicit null for the end of enironment
    envp[envc - 1] = NULL;

    execve(RTVS_RHOST_PATH, argv, envp);
    int err = errno;
    // TODO: Log output of explain_errno_execve
    _exit(err);
}

int authenticate_and_run(const picojson::object& json) {
    std::string msg_name(json[RTVS_JSON_MSG_NAME].get<std::string>());
    bool auth_only = msg_name == RTVS_MSG_AUTH_ONLY;

    std::string username(json[RTVS_JSON_MSG_USERNAME].get<std::string>());
    std::string password(json[RTVS_JSON_MSG_PASSWORD].get<std::string>());

    if (username.empty() || password.empty()) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_RTVS_ERROR, (double)RTVS_AUTH_NO_INPUT);
        } else {
            return RTVS_AUTH_NO_INPUT;
        }
    }

    pam_handle_t *pamh = nullptr;
    int err = 0;
    struct pam_conv conv = {
        (auth_only ? rtvs_conv : rtvs_conv_quiet),
        (void*)password.c_str()
    };

    bool pam_session_opened = false;
    SCOPE_WARDEN(pam_end, {
        if (pamh) {
            if (pam_session_opened) {
                err = pam_close_session(pamh, 0);
            }
            pam_end(pamh, err);
        }
    });

    if ((err = pam_start("rtvs", username.c_str(), &conv, &pamh)) != PAM_SUCCESS || pamh == nullptr) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    char pam_rhost[HOST_NAME_MAX + 1] = {};
    if ((err = gethostname(pam_rhost, sizeof(pam_rhost))) != 0) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_SYSTEM_ERROR, strerror(err));
        } else {
            return err;
        }
    }

    if ((err = pam_set_item(pamh, PAM_RHOST, pam_rhost)) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if ((err = pam_set_item(pamh, PAM_RUSER, "root")) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if ((err = pam_authenticate(pamh, 0)) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if ((err = pam_acct_mgmt(pamh, 0)) != PAM_SUCCESS) {
        // This can fail if the user's password has expired
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if ((err = pam_setcred(pamh, PAM_ESTABLISH_CRED)) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if ((err = pam_open_session(pamh, 0)) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }
    pam_session_opened = true;

    const char *pam_user = nullptr;
    if ((err = pam_get_item(pamh, PAM_USER, (const void **)&pam_user)) != PAM_SUCCESS) {
        if (auth_only) {
            write_json(RTVS_RESPONSE_TYPE_PAM_ERROR, pam_strerror(pamh, err));
        } else {
            return err;
        }
    }

    if (auth_only) {
        if (err == PAM_SUCCESS) {
            std::string user_home = get_user_home(pam_user);
            write_json(RTVS_RESPONSE_TYPE_RTVS_RESULT, user_home);
        }
        return err;
    }

    struct passwd *pw = getpwnam(pam_user);
    if (!pw) {
        return 1;
    }

    // we get here only for Auth and Run
    int pid = fork();
    if (pid == -1) {
        // TODO: log err and output of explain_errno_fork;
        int err = errno;
        return err;
    } else if (pid == 0) {
        if (initgroups(pw->pw_name, pw->pw_gid) == -1) {
            err = errno;
            _exit(err);
        }
        if (setgid(pw->pw_gid) == -1) {
            err = errno;
            _exit(err);
        }
        if (setuid(pw->pw_uid) == -1) {
            err = errno;
            _exit(err);
        }

        start_rhost(json);
    } else {
        int ws = 0;
        pid_t hpid = waitpid(pid, &ws, 0);
        if (hpid < 0) {
            err = errno;
            // TODO: log err and output of explain_errno_waitpid()
        }

        return WEXITSTATUS(ws);
    }

    return err;
}


int main(int argc, char **argv) {
    input = fdopen(dup(fileno(stdin)), "rb");
    setvbuf(input, NULL, _IONBF, 0);
    output = fdopen(dup(fileno(stdout)), "wb");
    setvbuf(output, NULL, _IONBF, 0);
    error = fdopen(dup(fileno(stderr)), "wb");
    setvbuf(error, NULL, _IONBF, 0);

    freopen("/dev/null", "rb", stdin);
    freopen("/dev/null", "wb", stdout);

    int option = 0;
    bool quiet = false;
    while ((option = getopt(argc, argv, "q")) != -1) {
        switch (option) {
        case 'q':
            quiet = true;
        default:
            break;
        }
    }

    picojson::value json_value;
    std::string json_err = picojson::parse(json_value, read_string(input));

    if (!json_err.empty()) {
        if (!quiet) {
            write_json(RTVS_RESPONSE_TYPE_JSON_ERROR, json_err);
        }
        return RTVS_AUTH_BAD_INPUT;
    }

    if (!json_value.is<picojson::object>()) {
        if (!quiet) {
            write_json(RTVS_RESPONSE_TYPE_RTVS_ERROR, "Error_RunAsUser_InputFormatInvalid");
        }
        return RTVS_AUTH_BAD_INPUT;
    }

    picojson::object json = json_value.get<picojson::object>();
    std::string msg_name(json[RTVS_JSON_MSG_NAME].get<std::string>());

    if (msg_name == RTVS_MSG_AUTH_ONLY || msg_name == RTVS_MSG_AUTH_AND_RUN) {
        return authenticate_and_run(json);
    } else {
        if (!quiet) {
            write_json(RTVS_RESPONSE_TYPE_RTVS_ERROR, "Error_RunAsUser_MessageTypeInvalid");
        }
        return RTVS_AUTH_BAD_INPUT;
    }
}

// g++ -std=c++14 -fexceptions -fpermissive -O0 -ggdb -I../src -I../lib/picojson -c ../src/*.c*
// g++ -g -o Microsoft.R.Host.RunAsUser.out ./*.o -lpthread -L/usr/lib/x86_64-linux-gnu -lpam