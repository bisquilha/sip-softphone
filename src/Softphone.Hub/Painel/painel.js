// Painel de administração do Softphone. Só usa a API do próprio Hub; o token fica na sessionStorage (some ao fechar o
// navegador) e vai no cabeçalho Authorization. Todo texto que vem do servidor entra por textContent, nunca como HTML.
(() => {
  'use strict';

  const CHAVE_TOKEN = 'softphone.token', CHAVE_NOME = 'softphone.nome', CHAVE_EXPIRA = 'softphone.expira';
  const $ = (id) => document.getElementById(id);
  const num = new Intl.NumberFormat('pt-BR');
  const num1 = new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const num2 = new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  // sessionStorage pode lançar (armazenamento bloqueado no navegador): o painel segue, só não lembra o acesso ao recarregar
  const sessao = {
    ler: (k) => { try { return sessionStorage.getItem(k); } catch { return null; } },
    gravar: (k, v) => { try { sessionStorage.setItem(k, v); } catch { /* fica só na memória desta página */ } },
    apagar: (k) => { try { sessionStorage.removeItem(k); } catch { /* nada a apagar */ } },
  };

  let token = sessao.ler(CHAVE_TOKEN);
  let painel = null;          // último /api/admin/painel
  let ramais = [];            // último /api/admin/ramais-app
  let abaAtual = 'geral';
  let foraDoAr = false;       // a última chamada nem chegou ao Hub
  const carregadas = new Set();
  const ultimaCarga = {};     // aba -> quando carregou certo pela última vez

  // ---------------------------------------------------------------- utilidades
  function el(tag, props, ...filhos) {
    const e = document.createElement(tag);
    if (props) {
      for (const [k, v] of Object.entries(props)) {
        if (v == null || v === false) continue;
        if (k === 'class') e.className = v;
        else if (k === 'text') e.textContent = v;
        else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
        else e.setAttribute(k, v === true ? '' : String(v));
      }
    }
    for (const f of filhos) if (f != null && f !== false) e.append(f instanceof Node ? f : document.createTextNode(String(f)));
    return e;
  }

  const ico = (codigo) => el('span', { class: 'ico', 'aria-hidden': 'true', text: String.fromCharCode(codigo) });
  const data = (iso) => iso ? new Date(iso) : null;
  const dataHora = (iso) => { const d = data(iso); return d ? d.toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : ''; };

  function haQuanto(iso) {
    const d = data(iso);
    if (!d) return '';
    const min = (Date.now() - d.getTime()) / 60000;
    if (min < 2) return 'agora há pouco';
    if (min < 60) return `há ${Math.floor(min)} min`;
    if (min < 1440) return `há ${Math.floor(min / 60)} h`;
    if (min < 2880) return 'ontem';
    return d.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' });
  }

  function duracao(seg) {
    seg = Math.max(0, Math.round(seg));
    const h = Math.floor(seg / 3600), m = Math.floor((seg % 3600) / 60), s = seg % 60;
    const dois = (n) => String(n).padStart(2, '0');
    return h > 0 ? `${h}:${dois(m)}:${dois(s)}` : `${m}:${dois(s)}`;
  }

  function status(texto, erro = false) {
    const s = $('status');
    s.textContent = texto;
    s.classList.toggle('erro', erro);
  }

  function ocupado(botao, sim) {
    if (!botao) return;
    botao.disabled = sim;
    botao.setAttribute('aria-busy', sim ? 'true' : 'false');
  }

  function limpar(no) { while (no.firstChild) no.removeChild(no.firstChild); }

  function esqueleto(tbody, colunas, linhas = 3) {
    if (tbody.querySelector('tr:not(.esqueleto)')) return; // já tem dados: ficam na tela até chegarem os novos
    limpar(tbody);
    for (let i = 0; i < linhas; i++) {
      const tr = el('tr', { class: 'esqueleto' });
      for (let c = 0; c < colunas; c++) tr.append(el('td'));
      tbody.append(tr);
    }
    tbody.closest('.tabela-rolagem').hidden = false;
  }

  // tabela sem linhas some (fica a mensagem de vazio), em vez de mostrar só o cabeçalho
  function ajustarTabela(tabela) {
    const tem = tabela.tBodies[0].rows.length > 0;
    tabela.closest('.tabela-rolagem').hidden = !tem;
    return tem;
  }

  const hora = (d) => d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });

  // ---------------------------------------------------------------- API
  class ErroApi extends Error {
    constructor(mensagem, codigo) { super(mensagem); this.codigo = codigo; }
  }

  async function mensagemDe(resposta) {
    try { const j = await resposta.json(); return (j && (j.erro || j.title)) || null; } catch { return null; }
  }

  function saudeTopo(estado, texto, dica) {
    const p = $('saude-ponto');
    p.className = 'ponto ' + estado;
    $('saude-texto').textContent = texto;
    $('saude-texto').parentElement.title = dica || texto;
  }

  // serviços que a Visão geral mostra em vermelho; o topo fica amarelo se algum estiver assim
  function problemas(p) {
    const n = [];
    if (!p.ldap || p.erroDiretorio) n.push('Diretório (AD)');
    if (!p.reunioes) n.push('Reuniões (Jitsi)');
    if (!p.transcricao) n.push('Transcrição');
    if (p.espacoLivreGb <= 5) n.push('Espaço livre');
    return n;
  }

  function topoNormal() {
    if (!painel) { saudeTopo('', 'Hub'); return; }
    const n = problemas(painel);
    if (n.length === 0) saudeTopo('ok', `Hub ${painel.versaoHub}`);
    else saudeTopo('espera', `Hub ${painel.versaoHub} · ${n.length === 1 ? '1 serviço' : n.length + ' serviços'} com problema`, 'Com problema: ' + n.join(', '));
  }

  const MSG_FORA = 'Servidor indisponível. Confira a rede ou se o Hub está no ar e clique em Atualizar.';

  // fetch com limite de tempo. 502/503/504 que não vêm em JSON são do nginx do softphone-server (o Hub atrás dele não respondeu)
  // e contam como Hub fora do ar; os do próprio Hub (ex.: teste da IA) vêm em JSON e seguem como erro comum.
  async function buscar(caminho, opcoes, tempoMs = 20000) {
    const ctl = new AbortController();
    const relogio = setTimeout(() => ctl.abort(), tempoMs);
    try {
      const r = await fetch(caminho, { ...opcoes, signal: ctl.signal });
      if ([502, 503, 504].includes(r.status) && !(r.headers.get('content-type') || '').includes('json')) throw new Error('proxy sem Hub');
      return r;
    } finally { clearTimeout(relogio); }
  }

  async function api(metodo, caminho, corpo, { texto = false, tempo } = {}) {
    const cab = { Accept: texto ? 'text/plain' : 'application/json' };
    if (token) cab.Authorization = 'Bearer ' + token;
    let body;
    if (corpo !== undefined) {
      if (texto) { cab['Content-Type'] = 'text/plain; charset=utf-8'; body = corpo; }
      else { cab['Content-Type'] = 'application/json'; body = JSON.stringify(corpo); }
    }
    let r;
    try {
      r = await buscar(caminho, { method: metodo, headers: cab, body, credentials: 'same-origin', cache: 'no-store' }, tempo);
    } catch {
      foraDoAr = true;
      saudeTopo('ruim', 'Hub fora do ar');
      throw new ErroApi(MSG_FORA, 0);
    }
    if (foraDoAr) { foraDoAr = false; topoNormal(); }
    if (r.status === 401) { sair('Sua sessão expirou. Entre de novo.'); throw new ErroApi('Sessão expirada.', 401); }
    if (r.status === 403) {
      const m = (await mensagemDe(r)) || 'Só a TI (administradores do Softphone) acessa o painel.';
      sair(m);
      throw new ErroApi(m, 403);
    }
    if (!r.ok) throw new ErroApi((await mensagemDe(r)) || `O servidor respondeu ${r.status}.`, r.status);
    if (texto) return r.text();
    const tipo = r.headers.get('content-type') || '';
    return tipo.includes('json') ? r.json() : null;
  }

  // ---------------------------------------------------------------- login
  function aviso(texto) {
    const a = $('login-aviso');
    a.hidden = !texto;
    a.textContent = texto || '';
  }

  function aceitar(resposta) {
    if (!resposta || !resposta.token) { aviso('O servidor não devolveu o acesso. Tente de novo.'); return; }
    if (!resposta.admin) {
      aviso(`Você entrou como ${resposta.eu?.nome || resposta.eu?.login || 'usuário'}, mas só a TI (administradores do Softphone) acessa este painel.`);
      return;
    }
    token = resposta.token;
    sessao.gravar(CHAVE_TOKEN, token);
    sessao.gravar(CHAVE_NOME, resposta.eu?.nome || resposta.eu?.login || '');
    sessao.gravar(CHAVE_EXPIRA, resposta.expiraEm || '');
    $('login-senha').value = '';
    aviso(null);
    mostrarPainel();
  }

  // 10/11/2026 (pedido do Ana): o painel só entra com usuário e senha de rede, validados direto no AD (LDAP) pelo Hub.
  // O login automático do Windows foi tirado: ele usava o usuário logado na máquina e não funcionava para quem recebeu acesso.
  async function entrarSenha(ev) {
    ev.preventDefault();
    const b = $('btn-entrar');
    const login = $('login-usuario').value.trim(), senha = $('login-senha').value;
    if (!login || !senha) return;
    aviso(null);
    ocupado(b, true);
    try {
      const r = await buscar('/api/entrar-senha', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({ login, senha }),
      });
      if (r.status === 401) { aviso('Usuário ou senha inválidos.'); $('login-senha').select(); return; }
      if (!r.ok) { aviso((await mensagemDe(r)) || `O servidor respondeu ${r.status}.`); return; }
      aceitar(await r.json());
    } catch {
      aviso('Servidor indisponível. Confira a rede ou se o Hub está no ar.');
    } finally { ocupado(b, false); }
  }

  function sair(mensagem) {
    token = null;
    painel = null;
    sessao.apagar(CHAVE_TOKEN);
    sessao.apagar(CHAVE_NOME);
    sessao.apagar(CHAVE_EXPIRA);
    carregadas.clear();
    $('tela-painel').hidden = true;
    $('topo-sessao').hidden = true;
    $('tela-login').hidden = false;
    aviso(mensagem || null);
    $('login-usuario').focus();
  }

  function mostrarPainel() {
    $('tela-login').hidden = true;
    $('tela-painel').hidden = false;
    $('topo-sessao').hidden = false;
    $('quem').textContent = sessao.ler(CHAVE_NOME) || '';
    saudeTopo('', 'Hub');
    trocarAba('geral');
  }

  // ---------------------------------------------------------------- abas
  const carregadores = {
    geral: carregarGeral,
    ia: carregarIa,
    comunicados: carregarComunicados,
    qualidade: carregarQualidade,
    ramais: carregarRamais,
    admins: carregarAdmins,
  };

  function trocarAba(nome) {
    abaAtual = nome;
    for (const b of document.querySelectorAll('.aba')) {
      const sel = b.dataset.aba === nome;
      b.setAttribute('aria-selected', sel ? 'true' : 'false');
      b.tabIndex = sel ? 0 : -1;
    }
    for (const s of document.querySelectorAll('.secao')) s.hidden = s.id !== 'aba-' + nome;
    if (!carregadas.has(nome)) carregar(nome);
  }

  async function carregar(nome) {
    carregadas.add(nome);
    status('Carregando...');
    const b = $('btn-atualizar');
    const secao = $('aba-' + nome);
    ocupado(b, true);
    try {
      await carregadores[nome]();
      ultimaCarga[nome] = new Date();
      secao.classList.remove('desatualizada');
      $('velho-' + nome)?.remove();
      status(`Atualizado às ${new Date().toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`);
    } catch (e) {
      carregadas.delete(nome);
      // Não deixa o esqueleto "carregando" para sempre; o que já estava na tela fica, marcado como velho, até a próxima tentativa.
      for (const tr of secao.querySelectorAll('tr.esqueleto')) tr.remove();
      for (const x of secao.querySelectorAll('ul[aria-busy="true"]')) x.setAttribute('aria-busy', 'false');
      for (const t of secao.querySelectorAll('.tabela')) ajustarTabela(t);
      if (e.codigo !== 401 && e.codigo !== 403) {
        secao.classList.add('desatualizada');
        avisoVelho(secao, nome, e.codigo === 0 ? 'o Hub não respondeu' : 'a atualização falhou');
        status(e.message, true);
      }
    } finally { ocupado(b, false); }
  }

  function avisoVelho(secao, nome, motivo) {
    let a = $('velho-' + nome);
    if (!a) { a = el('p', { class: 'aviso velho', id: 'velho-' + nome, role: 'status' }); secao.prepend(a); }
    const quando = ultimaCarga[nome];
    a.textContent = (quando ? `Dados das ${hora(quando)}: ${motivo}.` : `Sem dados: ${motivo}.`) + ' Clique em Atualizar para tentar de novo.';
  }

  async function buscarPainel() {
    painel = await api('GET', '/api/admin/painel');
    topoNormal();
    return painel;
  }

  // ---------------------------------------------------------------- visão geral
  async function carregarGeral() {
    const lista = $('geral-servicos');
    lista.setAttribute('aria-busy', 'true');
    esqueleto($('geral-tabela').tBodies[0], 6);
    const p = await buscarPainel();

    limpar(lista);
    const linha = (estado, nome, valor, detalhe) => lista.append(el('li', null,
      el('span', { class: 'ponto ' + estado, 'aria-hidden': 'true' }),
      el('span', { class: 'nome', text: nome }),
      el('span', { class: 'valor' + (estado === 'ruim' ? ' ruim' : ''), text: valor }),
      el('span', { class: 'detalhe', text: detalhe || '' })));
    const noAr = (Date.now() - new Date(p.iniciadoEm).getTime()) / 1000;
    const dias = Math.floor(noAr / 86400), horas = Math.floor((noAr % 86400) / 3600), mins = Math.floor((noAr % 3600) / 60);
    linha('ok', 'Servidor (Hub)', `versão ${p.versaoHub}`, `no ar há ${dias > 0 ? dias + ' d ' : ''}${horas} h ${mins} min`);
    linha('ok', 'Online agora', p.online === 1 ? '1 pessoa' : `${num.format(p.online)} pessoas`,
      `${p.clientes.length === 1 ? '1 pessoa já usou' : num.format(p.clientes.length) + ' pessoas já usaram'} o Softphone 1.1 ou mais novo`);
    linha(p.ldap && !p.erroDiretorio ? 'ok' : 'ruim', 'Diretório (AD)', p.ldap ? `${num.format(p.diretorio)} pessoas` : 'desligado',
      p.erroDiretorio ? 'erro: ' + p.erroDiretorio : p.sincronizadoEm ? 'sincronizado ' + haQuanto(p.sincronizadoEm) : '');
    linha(p.reunioes ? 'ok' : 'ruim', 'Reuniões (Jitsi)', p.reunioes ? 'ligadas' : 'desligadas', '');
    linha(p.transcricao ? 'ok' : 'ruim', 'Transcrição', p.transcricao ? 'ligada (local)' : 'desligada', 'whisper no próprio servidor');
    linha(p.resumo ? 'ok' : 'espera', 'Resumo por IA', p.resumo ? 'ligado' : 'desligado', p.chaveIa);
    linha('ok', 'Gravações guardadas', `${p.gravacoes === 1 ? '1 gravação' : num.format(p.gravacoes) + ' gravações'} · ${num.format(Math.round(p.gravacoesMb))} MB`, `apagadas depois de ${p.retencaoDias} dias`);
    linha(p.espacoLivreGb > 5 ? 'ok' : 'ruim', 'Espaço livre no servidor', `${num1.format(p.espacoLivreGb)} GB`, p.espacoLivreGb > 5 ? '' : 'pouco espaço (menos de 5 GB)');
    lista.setAttribute('aria-busy', 'false');

    const versoes = $('geral-versoes');
    limpar(versoes);
    const pares = Object.entries(p.versoes || {});
    if (pares.length === 0) versoes.append('Versões em uso (últimos 30 dias): nenhuma ainda (os apps 1.1 ou mais novos informam ao conectar).');
    else {
      versoes.append('Versões em uso (últimos 30 dias): ');
      pares.forEach(([v, n], i) => { if (i) versoes.append('  ·  '); versoes.append(el('strong', { text: v }), `: ${n}`); });
    }
    desenharClientes();
  }

  function desenharClientes() {
    if (!painel) return;
    const tbody = $('geral-tabela').tBodies[0];
    const filtro = $('geral-filtro').value.trim().toLocaleLowerCase('pt-BR');
    const maisNova = Object.keys(painel.versoes || {}).sort((a, b) => b.localeCompare(a, undefined, { numeric: true }))[0];
    const linhas = [...painel.clientes]
      .sort((a, b) => (b.online - a.online) || a.nome.localeCompare(b.nome, 'pt-BR'))
      .filter((c) => !filtro || [c.nome, c.login, c.unidade, c.versao, c.pc].some((x) => (x || '').toLocaleLowerCase('pt-BR').includes(filtro)));
    limpar(tbody);
    const frag = document.createDocumentFragment();
    for (const c of linhas) {
      frag.append(el('tr', null,
        el('td', null, el('span', { class: 'ponto ' + (c.online ? 'ok' : ''), title: c.online ? 'Online' : 'Offline' })),
        el('td', { class: 'forte', text: c.nome }),
        el('td', { class: 'sec', text: c.unidade || '' }),
        el('td', { class: c.versao && maisNova && c.versao !== maisNova ? 'forte' : '', text: c.versao || '?', title: c.versao !== maisNova ? 'Versão diferente da mais nova em uso' : null }),
        el('td', { class: 'sec', text: c.pc || '' }),
        el('td', { class: 'sec', text: c.online ? 'agora' : haQuanto(c.vistoEm) })));
    }
    tbody.append(frag);
    ajustarTabela($('geral-tabela'));
    const vazio = $('geral-vazio');
    vazio.hidden = linhas.length > 0;
    vazio.textContent = painel.clientes.length === 0
      ? 'Ninguém registrado ainda. Os apps 1.1 ou mais novos aparecem aqui ao conectar.'
      : 'Ninguém com esse filtro.';
  }

  // ---------------------------------------------------------------- IA (1.2.5: várias IAs, em ordem ou revezando)
  const TIPOS_IA = {
    gemini: { nome: 'Gemini', url: 'https://generativelanguage.googleapis.com/v1beta/openai' },
    openai: { nome: 'OpenAI', url: 'https://api.openai.com/v1' },
    xai: { nome: 'Grok', url: 'https://api.x.ai/v1' },
    deepseek: { nome: 'DeepSeek', url: 'https://api.deepseek.com/v1' },
    anthropic: { nome: 'Anthropic', url: 'https://api.anthropic.com' },
    outra: { nome: '', url: '' },
  };
  let provedores = null;   // último /api/admin/ia/provedores
  let editando = null;     // provedor em edição (null = novo)

  async function carregarIa() {
    const [p, pv] = await Promise.all([buscarPainel(), api('GET', '/api/admin/ia/provedores')]);
    $('ia-retencao').value = p.retencaoDias;
    desenharProvedores(pv);
  }

  function desenharProvedores(pv) {
    provedores = pv;
    const lista = $('ia-lista');
    limpar(lista);
    pv.lista.forEach((x, i) => {
      const tipo = $('pv-tipo').querySelector(`option[value="${x.tipo}"]`)?.textContent || x.tipo;
      const partes = [`${tipo} · ${x.modelo} · chave …${x.chaveFim}`, `uso: ${{ tudo: 'chat e ligações', chat: 'só chat', ligacoes: 'só ligações' }[x.uso || 'tudo']}`];
      if (!x.ligado) partes.push('desligada');
      else if (x.ultimaFalha && (!x.ultimoOk || x.ultimaFalha > x.ultimoOk)) partes.push(`falhou ${haQuanto(x.ultimaFalha)}: ${x.ultimoErro || ''}`);
      else if (x.ultimoOk) partes.push(`respondeu ${haQuanto(x.ultimoOk)}`);
      const ruim = x.ligado && x.ultimaFalha && (!x.ultimoOk || x.ultimaFalha > x.ultimoOk);
      const b = (texto, titulo, acao, extra) => el('button', { type: 'button', class: 'btn', text: texto, title: titulo, 'aria-label': `${titulo}: ${x.nome}`, onclick: (e) => acao(x, e.currentTarget), ...extra });
      lista.append(el('li', { class: x.ligado ? null : 'desligada' },
        el('div', { class: 'texto' },
          el('div', { class: 'titulo', text: `${i + 1}. ${x.nome}` }),
          el('div', { class: ruim ? 'sub ruim' : 'sub', text: partes.join(' · ') })),
        el('div', { class: 'acoes-linha' },
          b('↑', 'Subir', (y, bt) => mover(y, -1, bt), { disabled: i === 0 }),
          b('↓', 'Descer', (y, bt) => mover(y, 1, bt), { disabled: i === pv.lista.length - 1 }),
          b('Testar', 'Testar esta IA', testarProvedor),
          b(x.ligado ? 'Desligar' : 'Ligar', x.ligado ? 'Desligar' : 'Ligar', alternarProvedor),
          b('Editar', 'Editar', editarProvedor),
          b('Excluir', 'Excluir', excluirProvedor))));
    });
    lista.setAttribute('aria-busy', 'false');
    $('ia-vazio').hidden = pv.lista.length > 0;
    $('ia-modo').value = pv.modo;
  }

  async function mudarProvedores(fazer, botao, feito) {
    ocupado(botao, true);
    try {
      desenharProvedores(await fazer());
      carregadas.delete('geral');
      if (feito) status(feito);
      return true;
    } catch (e) {
      if (e.codigo === 409 || e.codigo === 404) { try { desenharProvedores(await api('GET', '/api/admin/ia/provedores')); } catch { /* fica a mensagem */ } }
      if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true);
      return false;
    } finally { ocupado(botao, false); }
  }

  const gravarOrdem = (ids, modo, botao, feito) =>
    mudarProvedores(() => api('PUT', '/api/admin/ia/provedores/ordem', { modo, ids }), botao, feito);

  function mover(x, delta, botao) {
    const ids = provedores.lista.map((p) => p.id);
    const i = ids.indexOf(x.id), j = i + delta;
    if (j < 0 || j >= ids.length) return;
    [ids[i], ids[j]] = [ids[j], ids[i]];
    gravarOrdem(ids, provedores.modo, botao, `${x.nome} agora é a ${j + 1}ª da lista.`);
  }

  function mudarModo() {
    const modo = $('ia-modo').value;
    gravarOrdem(provedores.lista.map((p) => p.id), modo, $('ia-modo'),
      modo === 'revezar' ? 'Revezando entre as IAs ligadas.' : 'Em ordem: a 1ª ligada responde; se falhar, a próxima.');
  }

  const corpoDe = (x, mudancas) => ({ id: x.id, nome: x.nome, tipo: x.tipo, url: x.url, modelo: x.modelo, chave: null, ligado: x.ligado, ...mudancas });

  function alternarProvedor(x, botao) {
    const ligadas = provedores.lista.filter((p) => p.ligado).length;
    if (x.ligado && ligadas === 1 && !confirm(`${x.nome} é a única IA ligada. Desligando, o resumo e o "Sugerir resposta" param para todos. Continuar?`)) return;
    mudarProvedores(() => api('POST', '/api/admin/ia/provedores', corpoDe(x, { ligado: !x.ligado })), botao, `${x.nome} ${x.ligado ? 'desligada' : 'ligada'}.`);
  }

  function excluirProvedor(x, botao) {
    if (!confirm(`Excluir a IA ${x.nome}? A chave dela é apagada do servidor.`)) return;
    mudarProvedores(() => api('DELETE', `/api/admin/ia/provedores/${encodeURIComponent(x.id)}`), botao, `${x.nome} excluída.`);
    if (editando?.id === x.id) fecharFormulario();
  }

  async function testarProvedor(x, botao) {
    const r = $('ia-teste');
    r.className = 'resultado';
    r.textContent = `Testando ${x.nome}...`;
    ocupado(botao, true);
    try {
      const t = await api('POST', `/api/admin/ia/provedores/${encodeURIComponent(x.id)}/testar`, undefined, { tempo: 200000 });
      r.className = 'resultado ' + (t.ok ? 'ok' : 'ruim');
      r.textContent = (t.ok ? 'Funcionou: ' : `${x.nome} não funcionou: `) + t.mensagem;
      desenharProvedores(await api('GET', '/api/admin/ia/provedores'));
    } catch (e) { r.className = 'resultado ruim'; r.textContent = 'Não funcionou: ' + e.message; }
    finally { ocupado(botao, false); }
  }

  async function testarIa() {
    const b = $('btn-testar-ia'), r = $('ia-teste');
    r.className = 'resultado';
    r.textContent = 'Testando pela lista (se a 1ª falhar, demora mais)...';
    ocupado(b, true);
    try {
      const t = await api('POST', '/api/admin/ia/testar', undefined, { tempo: 600000 });
      r.className = 'resultado ' + (t.ok ? 'ok' : 'ruim');
      r.textContent = (t.ok ? 'Funcionou. ' : 'Não funcionou: ') + t.mensagem;
      desenharProvedores(await api('GET', '/api/admin/ia/provedores'));
    } catch (e) {
      r.className = 'resultado ruim';
      r.textContent = 'Não funcionou: ' + e.message;
    } finally { ocupado(b, false); }
  }

  // ----- formulário de adicionar/editar
  function abrirFormulario(x) {
    editando = x;
    const f = $('form-provedor');
    $('pv-titulo').textContent = x ? `Editar ${x.nome}` : 'Adicionar IA';
    $('pv-tipo').value = x ? x.tipo : 'gemini';
    $('pv-tipo').disabled = !!x;
    $('pv-nome').value = x ? x.nome : TIPOS_IA.gemini.nome;
    $('pv-url').value = x ? x.url : TIPOS_IA.gemini.url;
    $('pv-chave').value = '';
    $('pv-chave').required = !x;
    $('pv-chave').placeholder = x ? `deixe vazio para manter a atual (…${x.chaveFim})` : 'cole a chave da API';
    $('pv-modelo').value = x ? x.modelo : '';
    $('pv-ligado').checked = x ? x.ligado : true;
    $('pv-uso').value = x ? (x.uso || 'tudo') : 'tudo';
    limpar($('pv-modelos'));
    $('pv-info').textContent = '';
    ajustarUrl();
    f.hidden = false;
    $(x ? 'pv-chave' : 'pv-tipo').focus();
  }

  function fecharFormulario() { $('form-provedor').hidden = true; editando = null; }
  const editarProvedor = (x) => abrirFormulario(x);

  // os tipos conhecidos já trazem o endereço; "Outra" pede o da API
  function ajustarUrl() {
    const t = $('pv-tipo').value;
    $('pv-url').required = t === 'outra';
    $('pv-url').placeholder = t === 'outra' ? 'https://api.exemplo.com/v1 (compatível com OpenAI)' : TIPOS_IA[t].url;
  }

  function trocarTipo() {
    const t = $('pv-tipo').value, antes = Object.values(TIPOS_IA);
    // só troca nome/endereço se ainda estavam com o padrão de outro tipo (não apaga o que a pessoa digitou)
    if (!$('pv-nome').value.trim() || antes.some((x) => x.nome === $('pv-nome').value.trim())) $('pv-nome').value = TIPOS_IA[t].nome;
    if (!$('pv-url').value.trim() || antes.some((x) => x.url && x.url === $('pv-url').value.trim())) $('pv-url').value = TIPOS_IA[t].url;
    $('pv-modelo').value = '';
    limpar($('pv-modelos'));
    ajustarUrl();
  }

  async function buscarModelosIa() {
    const b = $('btn-pv-modelos'), info = $('pv-info');
    info.className = 'resultado linha-toda';
    info.textContent = 'Perguntando à IA quais modelos ela tem...';
    ocupado(b, true);
    try {
      const lista = await api('POST', '/api/admin/ia/modelos',
        { id: editando?.id || null, tipo: $('pv-tipo').value, url: $('pv-url').value.trim() || null, chave: $('pv-chave').value.trim() || null }, { tempo: 45000 });
      const dl = $('pv-modelos');
      limpar(dl);
      for (const m of lista) dl.append(el('option', { value: m }));
      info.className = 'resultado linha-toda ' + (lista.length ? 'ok' : 'ruim');
      info.textContent = lista.length ? `${num.format(lista.length)} modelos: escolha no campo Modelo (clique nele ou comece a digitar).` : 'A IA não informou modelos. Digite o nome do modelo.';
      if (lista.length) $('pv-modelo').focus();
    } catch (e) {
      info.className = 'resultado linha-toda ruim';
      info.textContent = e.message;
    } finally { ocupado(b, false); }
  }

  async function salvarProvedor(ev) {
    ev.preventDefault();
    const corpo = {
      id: editando?.id || null,
      nome: $('pv-nome').value.trim(),
      tipo: $('pv-tipo').value,
      url: $('pv-url').value.trim() || null,
      modelo: $('pv-modelo').value.trim(),
      chave: $('pv-chave').value.trim() || null,
      ligado: $('pv-ligado').checked,
      uso: $('pv-uso').value,
    };
    const ok = await mudarProvedores(() => api('POST', '/api/admin/ia/provedores', corpo), ev.submitter,
      editando ? `${corpo.nome} salva.` : `${corpo.nome} adicionada no fim da lista. Use "Testar" para conferir.`);
    if (ok) { $('pv-chave').value = ''; fecharFormulario(); }
  }

  async function salvarIa(ev) {
    ev.preventDefault();
    const dias = parseInt($('ia-retencao').value, 10);
    if (!(dias >= 1 && dias <= 365)) { status('Retenção: de 1 a 365 dias.', true); return; }
    const antes = painel?.retencaoDias;
    if (antes && dias < antes && !confirm(`Diminuir a retenção de ${antes} para ${dias} dias?\n\nAs gravações de TODOS com mais de ${dias} dias (áudio, transcrição e resumo) serão apagadas em até 1 hora. Não dá para desfazer.`)) return;
    ocupado(ev.submitter, true);
    try {
      await api('PUT', '/api/admin/ia', { chave: null, apagarChave: false, modelo: null, retencaoDias: dias });
      carregadas.delete('geral');
      if (painel) painel.retencaoDias = dias;
      status('Retenção salva.');
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
    finally { ocupado(ev.submitter, false); }
  }

  // ---------------------------------------------------------------- comunicados
  async function carregarComunicados() {
    const para = $('com-para');
    if (para.options.length <= 1) {
      const unidades = await api('GET', '/api/admin/unidades');
      for (const u of unidades) para.append(el('option', { value: u, text: 'Unidade ' + u }));
    }
    const lista = $('com-lista');
    lista.setAttribute('aria-busy', 'true');
    const itens = await api('GET', '/api/admin/comunicados');
    limpar(lista);
    for (const it of itens) {
      const c = it.comunicado;
      const vencido = new Date(c.expiraEm) < new Date();
      const situacao = it.cancelado ? 'cancelado' : vencido ? 'vencido' : `vale até ${dataHora(c.expiraEm)}`;
      const ativo = !it.cancelado && !vencido;
      lista.append(el('li', null,
        el('div', { class: 'texto' },
          el('div', { class: 'titulo' },
            c.nivel === 'urgente' ? el('span', { class: 'etiqueta urgente', text: 'Urgente' }) : null,
            c.nivel === 'importante' ? el('span', { class: 'etiqueta importante', text: 'Importante' }) : null,
            !ativo ? el('span', { class: 'etiqueta encerrado', text: it.cancelado ? 'Cancelado' : 'Vencido' }) : null,
            c.titulo),
          el('div', { class: 'sub', text: `${dataHora(c.criadoEm)} · ${c.nomeAutor} · ${it.publico || (c.unidade ? 'unidade ' + c.unidade : 'todos')} · ${c.exigirCiencia === false ? 'sem exigir ciência · ' : ''}${situacao}` })),
        el('span', { class: 'lado', text: `${it.destinatarios} destinatários · ${it.entregues || 0} entregues · ${it.confirmaram} cientes` }),
        ativo ? el('button', { type: 'button', class: 'btn', text: 'Cancelar', title: 'Some da tela de todos', onclick: (e) => cancelarComunicado(c, e.currentTarget) }) : null));
    }
    lista.setAttribute('aria-busy', 'false');
    $('com-vazio').hidden = itens.length > 0;
  }

  // 1.3: público por unidade, setores e/ou grupo do AD; mostra o alcance antes de enviar; ciência opcional
  function corpoComunicado() {
    const setores = $('com-setores').value.split(',').map((s) => s.trim()).filter(Boolean);
    return {
      titulo: $('com-titulo').value.trim(), texto: $('com-texto').value.trim(), nivel: $('com-nivel').value, horas: parseInt($('com-horas').value, 10),
      unidade: $('com-para').value || null, setores, grupo: $('com-grupo').value.trim() || null, exigirCiencia: $('com-ciencia').checked,
    };
  }

  async function verAlcance() {
    const info = $('com-alcance');
    info.classList.remove('ruim');
    try {
      const a = await api('POST', '/api/comunicados/alcance', corpoComunicado());
      info.textContent = a.todos ? `Vai para TODOS: ${num.format(a.pessoas)} pessoas.` : `Vai para ${num.format(a.pessoas)} pessoa(s)` + (a.exemplos.length ? `, por exemplo: ${a.exemplos.join(', ')}.` : '.');
      return a;
    } catch (e) { info.classList.add('ruim'); info.textContent = e.message; throw e; }
  }

  async function enviarComunicado(ev) {
    ev.preventDefault();
    const corpo = corpoComunicado();
    if (!corpo.titulo || !corpo.texto) { status('Preencha título e mensagem.', true); return; }
    const nomeNivel = { info: 'informativo', importante: 'importante', urgente: 'URGENTE' }[corpo.nivel];
    let alcance;
    try { alcance = await verAlcance(); } catch { return; }
    if (!confirm(`Enviar agora um comunicado ${nomeNivel} para ${alcance.pessoas} pessoa(s)${alcance.todos ? ' (TODOS)' : ''}?\n\n${corpo.titulo}`)) return;
    const b = ev.submitter;
    ocupado(b, true);
    status('Enviando...');
    try {
      await api('POST', '/api/comunicados/enviar', corpo);
      $('com-titulo').value = ''; $('com-texto').value = '';
      await carregarComunicados();
      status('Comunicado enviado.');
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
    finally { ocupado(b, false); }
  }
  async function cancelarComunicado(c, botao) {
    if (!confirm(`Cancelar o comunicado "${c.titulo}"? Ele some da tela de todos.`)) return;
    ocupado(botao, true);
    try {
      await api('DELETE', `/api/admin/comunicados/${encodeURIComponent(c.id)}`);
      await carregarComunicados();
      status('Comunicado cancelado.');
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); ocupado(botao, false); }
  }

  // ---------------------------------------------------------------- qualidade
  const classeNota = (mos) => (mos >= 4.1 ? 'ok' : mos >= 3.6 ? '' : 'ruim');

  async function carregarQualidade() {
    const tbU = $('q-unidades').tBodies[0], tbP = $('q-piores').tBodies[0];
    esqueleto(tbU, 7);
    esqueleto(tbP, 8);
    const q = await api('GET', `/api/admin/qualidade?dias=${encodeURIComponent($('q-dias').value)}`);
    limpar(tbU);
    for (const u of q.porUnidade) {
      const pct = u.ligacoes ? Math.round((100 * u.ruins) / u.ligacoes) : 0;
      const semNada = u.ligacoes === 0;   // só ligações sem medição: não há média a mostrar
      const semMed = (txt) => el('td', { class: 'num sem-medicao', text: txt });
      tbU.append(el('tr', null,
        el('td', { class: 'forte', text: u.unidade }),
        el('td', { class: 'num', text: num.format(u.ligacoes) }),
        semNada ? semMed('Sem medição') : el('td', { class: 'num ' + classeNota(u.mosMedio), text: num2.format(u.mosMedio) }),
        semNada ? semMed('—') : el('td', { class: 'num', text: `${num.format(u.ruins)} (${pct}%)` }),
        semNada ? semMed('—') : el('td', { class: 'num', text: num1.format(u.perdaMedia) + '%' }),
        semNada ? semMed('—') : el('td', { class: 'num', text: `${num.format(Math.round(u.jitterMedio))} ms` }),
        el('td', { class: 'num' + (u.semMedicao ? ' forte' : ''), text: num.format(u.semMedicao || 0), title: u.semMedicao ? 'Ligações em que nenhum pacote de áudio chegou ou a coleta ficou incompleta' : null })));
    }
    limpar(tbP);
    for (const x of q.piores) {
      const medido = x.medido !== false;   // Hub antigo não manda o campo: trata como medido
      const sm = el('td', { class: 'num sem-medicao', text: 'Sem medição', title: 'Nenhum pacote de áudio chegou (sentido recebido): não é uma medição de perda' });
      tbP.append(el('tr', null,
        el('td', { class: 'sec', text: dataHora(x.inicio) }),
        el('td', { class: 'forte', text: x.nome }),
        el('td', { class: 'sec', text: x.unidade || '' }),
        el('td', { text: x.numero }),
        el('td', { class: 'num', text: duracao(x.duracaoSegundos) }),
        medido ? el('td', { class: 'num ruim', text: num1.format(x.mos) }) : sm,
        medido ? el('td', { class: 'num', text: num1.format(x.perdaPct) + '%' }) : el('td', { class: 'num sem-medicao', text: '—' }),
        medido ? el('td', { class: 'num', text: `${num.format(Math.round(x.jitterMs))} ms` }) : el('td', { class: 'num sem-medicao', text: '—' })));
    }
    const temMedicao = ajustarTabela($('q-unidades')), temRuim = ajustarTabela($('q-piores'));
    $('q-vazio').hidden = temMedicao;
    $('q-bloco-piores').hidden = !temMedicao;
    $('q-sem-ruins').hidden = temRuim;
  }

  // ---------------------------------------------------------------- ramais x AD
  function situacaoRamal(r) {
    if (!r.ativo) return 'fora do AD (inativo)';
    if (!r.ramalAprendido) return 'só no AD (ainda não usou o Softphone)';
    if (!r.ramalAd) return 'falta no AD: gravar';
    return r.ramalAprendido === r.ramalAd ? 'igual' : 'diferente: atualizar o AD';
  }
  const diferente = (r) => r.ativo && r.ramalAprendido && r.ramalAprendido !== r.ramalAd;

  async function carregarRamais() {
    esqueleto($('r-tabela').tBodies[0], 5);
    ramais = await api('GET', '/api/admin/ramais-app');
    desenharRamais();
  }

  function visiveisRamais() { return $('r-diferentes').checked ? ramais.filter(diferente) : ramais; }

  function desenharRamais() {
    const tbody = $('r-tabela').tBodies[0];
    const lista = visiveisRamais();
    limpar(tbody);
    const frag = document.createDocumentFragment();
    for (const r of lista) {
      frag.append(el('tr', null,
        el('td', { class: 'forte', text: r.nome }),
        el('td', { class: 'sec', text: r.login }),
        el('td', { class: 'num forte', text: r.ramalAprendido || '—' }),
        el('td', { class: 'num', text: r.ramalAd || '—' }),
        el('td', { class: diferente(r) ? 'ruim' : 'sec', text: situacaoRamal(r) })));
    }
    tbody.append(frag);
    const vazio = $('r-vazio');
    vazio.hidden = ajustarTabela($('r-tabela'));
    vazio.textContent = ramais.length === 0
      ? 'Nenhum ramal para comparar ainda.'
      : 'Tudo batendo: nenhum ramal diferente entre o Softphone e o AD.';
    if (abaAtual === 'ramais') status(`${lista.length} de ${ramais.length} ramais.`);
  }

  async function copiarRamais() {
    const csv = ['Nome;Login;Ramal Softphone;Ramal AD;Situacao',
      ...visiveisRamais().map((r) => [r.nome, r.login, r.ramalAprendido || '', r.ramalAd || '', situacaoRamal(r)].map((x) => String(x).replace(/[;\r\n]/g, ' ')).join(';'))].join('\r\n');
    try { await navigator.clipboard.writeText(csv); status('Copiado. Cole no Excel.'); }
    catch { status('O navegador não deixou copiar. Tente de novo com a página em foco.', true); }
  }

  // ---------------------------------------------------------------- administradores
  async function carregarAdmins() {
    const p = await buscarPainel();
    try { if (ramais.length === 0) ramais = await api('GET', '/api/admin/ramais-app'); } catch { /* sugestões são opcionais */ }
    desenharGrupos(p.gruposAdmin || []);
    desenharAdmins(p.admins);
    const nomes = new Map();
    for (const c of p.clientes) nomes.set(c.login, c.nome);
    for (const r of ramais) nomes.set(r.login, r.nome);
    const dl = $('adm-pessoas');
    limpar(dl);
    for (const [login, nome] of [...nomes].sort((a, b) => a[1].localeCompare(b[1], 'pt-BR'))) dl.append(el('option', { value: login, label: nome }));
  }

  function nomeDe(login) {
    const c = painel?.clientes.find((x) => x.login.toLowerCase() === login.toLowerCase());
    const r = ramais.find((x) => x.login.toLowerCase() === login.toLowerCase());
    return c?.nome || r?.nome || login;
  }

  function desenharAdmins(admins) {
    const lista = $('adm-lista');
    limpar(lista);
    for (const login of admins) {
      lista.append(el('li', null,
        el('div', { class: 'texto' }, el('div', { class: 'titulo', text: nomeDe(login) }), el('div', { class: 'sub', text: login })),
        el('button', { type: 'button', class: 'btn', text: 'Tirar', onclick: (e) => tirarAdmin(login, e.currentTarget) })));
    }
  }

  // `mudar` recebe a lista atual do servidor (relida agora: não zera a lista se a tela estiver velha nem desfaz a
  // mudança de outro admin) e devolve a lista a gravar
  async function gravarAdmins(mudar, botao) {
    ocupado(botao, true);
    try {
      const atuais = (await buscarPainel()).admins;
      const novos = await api('PUT', '/api/admin/admins', mudar(atuais));
      if (painel) painel.admins = novos;
      desenharAdmins(novos);
      return novos;
    } finally { ocupado(botao, false); }
  }

  async function acrescentarAdmin(ev) {
    ev.preventDefault();
    const login = $('adm-pessoa').value.trim().toLowerCase();
    if (!login) return;
    try {
      const novos = await gravarAdmins((atuais) => [...atuais, login], ev.submitter);
      if (novos.some((x) => x.toLowerCase() === login)) { status(`${nomeDe(login)} agora é administrador.`); $('adm-pessoa').value = ''; }
      else status(`Não achei "${login}" no diretório. Use o login de rede (ex.: ana.souza).`, true);
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
  }

  async function tirarAdmin(login, botao) {
    const eu = (sessao.ler(CHAVE_NOME) || '').toLowerCase();
    const aviso1 = nomeDe(login).toLowerCase() === eu ? '\n\nÉ você mesmo: se não estiver no hub.env, perde o acesso a este painel.' : '';
    if (!confirm(`Tirar ${nomeDe(login)} dos administradores?${aviso1}`)) return;
    try {
      const novos = await gravarAdmins((atuais) => atuais.filter((x) => x.toLowerCase() !== login.toLowerCase()), botao);
      if (novos.some((x) => x.toLowerCase() === login.toLowerCase())) status(`${nomeDe(login)} está no hub.env (Hub__Admins) e continua administrador.`, true);
      else status(`${nomeDe(login)} não é mais administrador.`);
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
  }

  // ---------------------------------------------------------------- grupos do AD (Hub 1.2.3)
  const membrosTexto = (n) => (n === 1 ? '1 membro' : `${num.format(n)} membros`);

  function desenharGrupos(grupos) {
    const lista = $('grp-lista');
    limpar(lista);
    for (const g of grupos) {
      lista.append(el('li', null,
        el('div', { class: 'texto' },
          el('div', { class: 'titulo', text: g.nome }),
          el('div', { class: g.erro ? 'sub ruim' : 'sub', text: g.erro ? `${membrosTexto(g.membros)} · não lido agora: ${g.erro}` : membrosTexto(g.membros) })),
        el('button', { type: 'button', class: 'btn', text: 'Tirar', onclick: (e) => tirarGrupo(g.nome, e.currentTarget) })));
    }
    $('grp-vazio').hidden = grupos.length > 0;
  }

  // como nos administradores: relê a lista do servidor antes de gravar (não desfaz a mudança de outro admin)
  async function gravarGrupos(mudar, botao) {
    ocupado(botao, true);
    try {
      const atuais = ((await buscarPainel()).gruposAdmin || []).map((g) => g.nome);
      const novos = await api('PUT', '/api/admin/grupos-admin', mudar(atuais));
      if (painel) painel.gruposAdmin = novos;
      desenharGrupos(novos);
      return novos;
    } finally { ocupado(botao, false); }
  }

  async function acrescentarGrupo(ev) {
    ev.preventDefault();
    const nome = $('grp-nome').value.trim();
    if (!nome) return;
    status(`Procurando o grupo ${nome} no AD...`);
    try {
      const novos = await gravarGrupos((atuais) => [...atuais, nome], ev.submitter);
      const g = novos.find((x) => x.nome.toLowerCase() === nome.toLowerCase());
      status(`${g?.nome || nome}: ${membrosTexto(g?.membros || 0)} agora ${g?.membros === 1 ? 'é administrador' : 'são administradores'}.`);
      $('grp-nome').value = '';
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
  }

  async function tirarGrupo(nome, botao) {
    if (!confirm(`Tirar o grupo ${nome}?\n\nOs membros dele deixam de ser administradores (a não ser que estejam em outro grupo da lista ou entre as pessoas).`)) return;
    try {
      await gravarGrupos((atuais) => atuais.filter((x) => x.toLowerCase() !== nome.toLowerCase()), botao);
      status(`Grupo ${nome} tirado.`);
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
  }

  // 1.2.4: lista de grupos do AD para escolher (pesquisa no servidor enquanto digita; só a última resposta vale)
  let buscaGrupoVez = 0;
  let buscaGrupoTimer = null;
  async function buscarGrupos(termo) {
    const vez = ++buscaGrupoVez;
    const info = $('grp-busca-info');
    try {
      const achados = await api('GET', `/api/admin/diretorio/grupos?busca=${encodeURIComponent(termo)}`);
      if (vez !== buscaGrupoVez) return;
      const dl = $('grp-sugestoes');
      limpar(dl);
      for (const g of achados) dl.append(el('option', { value: g.nome, label: g.descricao || g.nome }));
      info.classList.remove('ruim');
      info.textContent = achados.length === 0 ? (termo ? `Nenhum grupo do AD com "${termo}".` : 'Nenhum grupo encontrado no AD.')
        : achados.length >= 50 ? 'Mostrando os 50 primeiros: digite mais do nome para achar o grupo.'
          : `${achados.length === 1 ? '1 grupo encontrado' : `${num.format(achados.length)} grupos encontrados`}: escolha na lista do campo.`;
    } catch (e) {
      if (vez !== buscaGrupoVez || e.codigo === 401 || e.codigo === 403) return;
      info.classList.add('ruim');
      info.textContent = `Não deu para listar os grupos: ${e.message} Ainda dá para digitar o nome exato.`;
    }
  }
  function buscarGruposDepois() {
    clearTimeout(buscaGrupoTimer);
    const termo = $('grp-nome').value.trim();
    // escolheu uma opção da lista: não pesquisa de novo
    if ([...$('grp-sugestoes').options].some((o) => o.value.toLowerCase() === termo.toLowerCase())) return;
    buscaGrupoTimer = setTimeout(() => buscarGrupos(termo), 300);
  }

  async function sincronizarAgora() {
    const b = $('btn-sincronizar');
    ocupado(b, true);
    status('Lendo o AD...');
    try {
      const r = await api('POST', '/api/admin/diretorio/sincronizar', undefined, { tempo: 120000 });
      carregadas.delete('geral');
      await carregar('geral');
      status(`AD sincronizado: ${num.format(r.total)} pessoas, às ${hora(new Date())}.`);
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
    finally { ocupado(b, false); }
  }

  // ---------------------------------------------------------------- ligações
  $('btn-sincronizar').addEventListener('click', sincronizarAgora);
  $('form-grupo').addEventListener('submit', acrescentarGrupo);
  $('grp-nome').addEventListener('input', buscarGruposDepois);
  $('grp-nome').addEventListener('focus', () => { if ($('grp-sugestoes').options.length === 0) buscarGrupos($('grp-nome').value.trim()); });
  $('form-senha').addEventListener('submit', entrarSenha);
  $('btn-sair').addEventListener('click', () => sair(null));
  $('btn-atualizar').addEventListener('click', () => carregar(abaAtual));
  $('ia-modo').addEventListener('change', mudarModo);
  $('btn-ia-nova').addEventListener('click', () => abrirFormulario(null));
  $('form-provedor').addEventListener('submit', salvarProvedor);
  $('btn-pv-cancelar').addEventListener('click', fecharFormulario);
  $('pv-tipo').addEventListener('change', trocarTipo);
  $('btn-pv-modelos').addEventListener('click', buscarModelosIa);
  $('form-ia').addEventListener('submit', salvarIa);
  $('btn-testar-ia').addEventListener('click', testarIa);
  $('form-comunicado').addEventListener('submit', enviarComunicado);
  $('btn-com-alcance').addEventListener('click', () => verAlcance().catch(() => {}));
  $('q-dias').addEventListener('change', () => carregar('qualidade'));
  $('r-diferentes').addEventListener('change', desenharRamais);
  $('btn-r-copiar').addEventListener('click', copiarRamais);
  $('form-admin').addEventListener('submit', acrescentarAdmin);
  $('geral-filtro').addEventListener('input', desenharClientes);

  const abas = [...document.querySelectorAll('.aba')];
  for (const b of abas) {
    b.addEventListener('click', () => trocarAba(b.dataset.aba));
    b.addEventListener('keydown', (e) => {
      const i = abas.indexOf(b);
      const alvo = e.key === 'ArrowRight' ? abas[(i + 1) % abas.length] : e.key === 'ArrowLeft' ? abas[(i - 1 + abas.length) % abas.length] : null;
      if (alvo) { e.preventDefault(); alvo.focus(); trocarAba(alvo.dataset.aba); }
    });
  }

  // 1.3: gancho para painel-v13.js (Visão geral por módulos, Artigos, Auditoria, copiloto), que roda logo depois deste arquivo
  window.EmbaPainel = {
    api, el, $, status, num, num1, num2, dataHora, haQuanto, duracao, limpar, ocupado, esqueleto, ajustarTabela, carregadores,
    recarregar: (nome) => carregadas.delete(nome), token: () => token,
  };

  // ---------------------------------------------------------------- início
  const expira = sessao.ler(CHAVE_EXPIRA);
  if (token && (!expira || new Date(expira) > new Date())) mostrarPainel();
  else { if (token) sair('Sua sessão expirou. Entre de novo.'); else $('login-usuario').focus(); }
})();
