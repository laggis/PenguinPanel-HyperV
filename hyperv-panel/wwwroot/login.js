window.penguinPanelLogin = window.penguinPanelLogin || {
  readFields: function () {
    const username = document.getElementById('penguin-login-username');
    const password = document.getElementById('penguin-login-password');
    return {
      username: username && typeof username.value === 'string' ? username.value : '',
      password: password && typeof password.value === 'string' ? password.value : ''
    };
  }
};
