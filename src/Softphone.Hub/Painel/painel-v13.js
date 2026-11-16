// 1.3 (Claude Code, 10/11/2026): Visão geral por módulos (Telefonia, Sistema), Auditoria e perfis de acesso. 1.3.3 (11/11/2026): sem os módulos de artigos,
// anotações e copiloto, que saíram do Softphone. Roda depois de painel.js e usa o gancho window.EmbaPainel. Todo texto do servidor entra por textContent.
(() => {
  'use strict';
  const P = window.EmbaPainel;
  if (!P) return;
  const { api, el, $, status, num, num1, dataHora, haQuanto, duracao, limpar, ocupado, esqueleto, ajustarTabela, carregadores } = P;

  // ------------------------------------------------------------ Visão geral por módulos
  let periodo = 'hoje';
  let aberto = null;   // chave do indicador cujo detalhe está na tela

  const NOME_RESULTADO = { Atendida: 'Atendida', Perdida: 'Perdida', Recusada: 'Recusada', Ocupado: 'Ocupado', NaoAtendida: 'Não atendida',
    Falhou: 'Falhou', Cancelada: 'Cancelada', Transferida: 'Transferida', Encaminhada: 'Encaminhada', Nenhum: '—' };

  const cubo = () => el('span', { class: 'cubo', 'aria-hidden': 'true' }, el('i', { class: 'f' }), el('i', { class: 'd' }), el('i', { class: 't' }));
  const hhmmss = (seg) => (seg == null ? '—' : duracao(seg));

  function kpi(chave, rotulo, valor, { estado, alerta, semdado, detalhe } = {}) {
    const b = el('button', { type: 'button', class: 'kpi' + (alerta ? ' alerta' : '') + (semdado ? ' semdado' : ''), 'aria-pressed': aberto === chave ? 'true' : 'false',
      disabled: !detalhe, 'data-chave': chave, title: detalhe ? 'Ver a lista que compõe este número' : null },
    el('span', { class: 'valor', text: valor }), el('span', { class: 'rotulo', text: rotulo }), estado ? el('span', { class: 'estado', text: estado }) : null);
    if (detalhe) b.addEventListener('click', () => abrirDetalhe(chave, rotulo, detalhe));
    return b;
  }

  function modulo(classe, titulo, sub, kpis) {
    return el('section', { class: 'modulo ' + classe, 'aria-label': titulo }, el('h3', null, cubo(), el('span', null, titulo), el('span', { class: 'sub', text: sub })), el('div', { class: 'kpis' }, ...kpis));
  }

  async function desenharModulos() {
    const caixa = $('g-modulos');
    caixa.setAttribute('aria-busy', 'true');
    const [r, p] = await Promise.all([api('GET', `/api/admin/resumo?periodo=${encodeURIComponent(periodo)}`), api('GET', '/api/admin/painel')]);
    limpar(caixa);
    const sem = !r.haDadosDeChamadas;   // nenhum app mandou chamadas ainda: "sem dados" não é "zero"
    const n = (v, filtro, def) => sem
      ? { valor: 'Sem dados', opcoes: { semdado: true, estado: 'os apps 1.2 ainda não enviaram chamadas' } }
      : { valor: num.format(v), opcoes: { estado: v === 0 ? 'nenhuma ocorrência' : null, detalhe: v > 0 ? { filtro, def } : null } };
    const rec = n(r.recebidas, 'direcao=entrada', 'Chamadas recebidas pelos ramais com o Softphone no período (todas, atendidas ou não).');
    const real = n(r.realizadas, 'direcao=saida', 'Chamadas feitas pelos ramais com o Softphone no período.');
    const perd = n(r.perdidas, 'resultado=Perdida', 'Recebidas que ninguém atendeu, inclusive por "não perturbe" ou número bloqueado.');
    const recu = n(r.recusadas, 'resultado=Recusada', 'Recebidas que a pessoa recusou na tela.');
    const falh = n(r.falhas, 'resultado=Falhou,Ocupado,NaoAtendida', 'Chamadas feitas que não completaram: falha, ocupado ou sem resposta.');
    const tran = n(r.transferidas, 'transferida=1', 'Chamadas encerradas por transferência (cega ou assistida). Também contam como atendidas.');
    const medio = sem ? { valor: 'Sem dados', opcoes: { semdado: true } }
      : { valor: hhmmss(r.tempoMedioSegundos), opcoes: { estado: 'só conversas com duração', detalhe: { filtro: 'resultado=Atendida', def: 'Média da duração das conversas (do atender ao desligar). Chamadas sem conversa não entram.' } } };

    caixa.append(
      modulo('telefonia', 'Telefonia', `${num.format(r.chamadasTotal)} chamadas no período`, [
        kpi('rec', 'Recebidas', rec.valor, rec.opcoes), kpi('real', 'Realizadas', real.valor, real.opcoes),
        kpi('perd', 'Perdidas', perd.valor, { ...perd.opcoes, alerta: !sem && r.perdidas > 0 }), kpi('recu', 'Recusadas', recu.valor, recu.opcoes),
        kpi('falh', 'Falhas ao ligar', falh.valor, { ...falh.opcoes, alerta: !sem && r.falhas > 0 }), kpi('tran', 'Transferências', tran.valor, tran.opcoes),
        kpi('medio', 'Duração média da conversa', medio.valor, medio.opcoes),
        kpi('fila', 'Fila de espera', 'Sem dados', { semdado: true, estado: 'o PABX atual não informa filas ao Softphone' }),
      ]),
      modulo('sistema', 'Sistema', `Hub ${p.versaoHub}`, [
        kpi('onl', 'Softphone online agora', num.format(r.ramaisOnline), { estado: `de ${num.format(r.cadastrados)} pessoas no diretório` }),
        kpi('reg', 'Ramal registrado no PABX', 'Não medido', { semdado: true, estado: 'o Hub vê o app conectado, não o registro SIP' }),
        kpi('erros', 'Erros nas últimas 24 h', num.format(r.errosRecentes), { alerta: r.errosRecentes > 0, estado: r.errosRecentes > 0 ? 'ver aba Auditoria' : 'nenhum erro registrado' }),
        kpi('disco', 'Espaço livre', `${num1.format(p.espacoLivreGb)} GB`, { alerta: p.espacoLivreGb <= 5 }),
        kpi('trans', 'Transcrição', p.transcricao ? 'ligada' : 'desligada', { alerta: !p.transcricao, estado: 'serviço local' }),
        kpi('ia', 'IA externa', p.resumo ? 'ligada' : 'desligada', { estado: p.chaveIa }),
      ]));
    caixa.setAttribute('aria-busy', 'false');
    $('g-atualizado').textContent = `Atualizado às ${new Date().toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
    if (aberto && !$('g-detalhe').hidden) fecharDetalhe(); // período mudou: o detalhe aberto não vale mais
  }

  function fecharDetalhe() {
    aberto = null;
    $('g-detalhe').hidden = true;
    for (const b of document.querySelectorAll('.kpi[aria-pressed="true"]')) b.setAttribute('aria-pressed', 'false');
  }

  async function abrirDetalhe(chave, titulo, { filtro, def }) {
    aberto = chave;
    for (const b of document.querySelectorAll('.kpi')) b.setAttribute('aria-pressed', b.dataset.chave === chave ? 'true' : 'false');
    $('g-detalhe').hidden = false;
    $('g-detalhe-titulo').textContent = `${titulo} (${$('g-periodo').selectedOptions[0].textContent.toLowerCase()})`;
    $('g-detalhe-def').textContent = def;
    const tb = $('g-detalhe-tabela').tBodies[0];
    esqueleto(tb, 7);
    $('g-detalhe').scrollIntoView({ block: 'nearest', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    try {
      // os mesmos filtros do número: período + critério do indicador
      const lista = await api('GET', `/api/admin/chamadas?periodo=${encodeURIComponent(periodo)}&${filtro}`);
      if (aberto !== chave) return;
      limpar(tb);
      for (const c of lista) {
        tb.append(el('tr', null,
          el('td', { class: 'sec', text: dataHora(c.inicio) }), el('td', { class: 'forte', text: c.nome }), el('td', { class: 'sec', text: c.unidade || '' }),
          el('td', { text: c.numero }), el('td', { text: c.direcao === 'entrada' ? 'Recebida' : 'Realizada' }),
          el('td', { text: (NOME_RESULTADO[c.resultado] || c.resultado) + (c.transferida && c.resultado !== 'Transferida' ? ' · transferida' : ''), title: c.motivo || null }),
          el('td', { class: 'num', text: c.duracaoSegundos > 0 ? duracao(c.duracaoSegundos) : '—' })));
      }
      $('g-detalhe-vazio').hidden = ajustarTabela($('g-detalhe-tabela'));
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }
  }

  const geralOriginal = carregadores.geral;
  carregadores.geral = async () => { await Promise.all([geralOriginal(), desenharModulos()]); };
  $('g-periodo').addEventListener('change', () => { periodo = $('g-periodo').value; desenharModulos().catch((e) => { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }); });
  $('g-detalhe-fechar').addEventListener('click', fecharDetalhe);
  // painel.js já pode ter começado a carregar a Visão geral antes deste arquivo trocar o carregador: completa aqui
  if (!$('tela-painel').hidden) desenharModulos().catch(() => { /* o carregador normal mostra o erro na próxima atualização */ });

  // ------------------------------------------------------------ Auditoria
  function parametrosAuditoria() {
    const q = new URLSearchParams();
    const de = $('aud-de').value, ate = $('aud-ate').value;
    if (de) q.set('de', new Date(de + 'T00:00:00').toISOString());
    if (ate) q.set('ate', new Date(new Date(ate + 'T00:00:00').getTime() + 86400000).toISOString());   // "até" inclui o dia inteiro
    for (const [id, nome] of [['aud-usuario', 'usuario'], ['aud-acao', 'acao'], ['aud-gravidade', 'gravidade'], ['aud-ramal', 'ramal'], ['aud-texto', 'q']]) { const v = $(id).value.trim(); if (v) q.set(nome, v); }
    return q;
  }

  async function carregarAuditoria() {
    const tb = $('aud-tabela').tBodies[0];
    esqueleto(tb, 6);
    const q = parametrosAuditoria();
    q.set('limite', '500');
    const lista = await api('GET', `/api/admin/auditoria?${q}`);
    limpar(tb);
    for (const a of lista) {
      tb.append(el('tr', null,
        el('td', { class: 'sec', text: new Date(a.em).toLocaleString('pt-BR') }), el('td', { class: 'forte', text: a.usuario }), el('td', { text: a.acao }),
        el('td', { text: a.alvo }), el('td', { class: 'num ' + (a.status >= 400 ? 'status-erro' : 'status-ok'), text: String(a.status) }),
        el('td', { class: 'sec', text: a.detalhe || '' })));
    }
    $('aud-vazio').hidden = ajustarTabela($('aud-tabela'));
  }

  async function exportarAuditoria() {
    const b = $('btn-aud-csv');
    ocupado(b, true);
    try {
      const resp = await fetch(`/api/admin/auditoria.csv?${parametrosAuditoria()}`, { headers: { Authorization: 'Bearer ' + P.token() }, cache: 'no-store' });
      if (!resp.ok) throw new Error(`O servidor respondeu ${resp.status}.`);
      const url = URL.createObjectURL(await resp.blob());
      const a = el('a', { href: url, download: `auditoria-${new Date().toISOString().slice(0, 10)}.csv` });
      document.body.append(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
      status('Auditoria exportada.');
    } catch (e) { status(e.message || 'Não foi possível exportar.', true); }
    finally { ocupado(b, false); }
  }

  carregadores.auditoria = carregarAuditoria;
  $('form-aud').addEventListener('submit', (ev) => { ev.preventDefault(); P.recarregar('auditoria'); carregarAuditoria().catch((e) => { if (e.codigo !== 401 && e.codigo !== 403) status(e.message, true); }); });
  $('btn-aud-csv').addEventListener('click', exportarAuditoria);

  // ------------------------------------------------------------ perfis de acesso (aba Administradores)
  let regras = [];
  function desenharRegras() {
    const tb = $('acc-tabela').tBodies[0];
    limpar(tb);
    regras.forEach((r, i) => {
      tb.append(el('tr', null,
        el('td', { class: 'forte', text: r.grupo }), el('td', { text: r.perfil === 'supervisor' ? 'Supervisor' : 'Atendente' }),
        el('td', { class: 'sec', text: (r.setores || []).join(', ') || '(o setor da própria pessoa)' }),
        el('td', null, el('button', { type: 'button', class: 'btn perigo', text: 'Remover', 'aria-label': `Remover a regra do grupo ${r.grupo}`, onclick: () => gravarRegras(regras.filter((_, k) => k !== i)) }))));
    });
    $('acc-vazio').hidden = ajustarTabela($('acc-tabela'));
  }
  async function gravarRegras(nova) {
    try { regras = await api('PUT', '/api/admin/acesso', nova); desenharRegras(); $('acc-resultado').textContent = 'Regras salvas.'; }
    catch (e) { if (e.codigo !== 401 && e.codigo !== 403) $('acc-resultado').textContent = e.message; }
  }
  async function carregarRegras() { regras = await api('GET', '/api/admin/acesso'); desenharRegras(); }
  const adminsOriginal = carregadores.admins;
  carregadores.admins = async () => { await adminsOriginal(); await carregarRegras(); };
  $('form-acesso').addEventListener('submit', (ev) => {
    ev.preventDefault();
    const setores = $('acc-setores').value.split(',').map((s) => s.trim()).filter(Boolean);
    gravarRegras([...regras, { grupo: $('acc-grupo').value.trim(), perfil: $('acc-perfil').value, setores }]);
    $('acc-grupo').value = ''; $('acc-setores').value = '';
  });
  $('form-perfil-consulta').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const login = $('acc-consulta').value.trim();
    if (!login) return;
    try {
      const p = await api('GET', `/api/admin/acesso/consulta?login=${encodeURIComponent(login)}`);
      $('acc-resultado').textContent = `${p.login}: ${p.perfil}` + (p.setores.length ? ` · setores: ${p.setores.join(', ')}` : '') + (p.grupos.length ? ` · por ${p.grupos.join(', ')}` : '');
    } catch (e) { if (e.codigo !== 401 && e.codigo !== 403) $('acc-resultado').textContent = e.message; }
  });
  // ------------------------------------------------------------ automações do chat
  let regrasAut = [];
  const NOME_GATILHO = { palavra: 'Palavra-chave', chamada_perdida: 'Chamada perdida', boas_vindas: 'Boas-vindas' };
  const NOME_ACAO = { responder: 'Responder', notificar: 'Avisar a pessoa', encaminhar: 'Encaminhar' };
  function desenharAutomacoes() {
    const tb = $('aut-tabela').tBodies[0];
    limpar(tb);
    regrasAut.forEach((r, i) => {
      tb.append(el('tr', null,
        el('td', { class: 'forte', text: r.nome }),
        el('td', { text: NOME_GATILHO[r.gatilho] + (r.palavras.length ? ': ' + r.palavras.join(', ') : '') }),
        el('td', { text: NOME_ACAO[r.acao] }), el('td', { class: 'sec', text: r.setores.join(', ') || 'todos' }), el('td', { class: 'sec', text: r.responsavel || '' }),
        el('td', null, el('button', { type: 'button', class: 'btn', text: r.ligada ? 'Desligar' : 'Ligar', onclick: () => gravarAutomacoes(regrasAut.map((x, k) => (k === i ? { ...x, ligada: !x.ligada } : x))) })),
        el('td', null, el('button', { type: 'button', class: 'btn perigo', text: 'Remover', 'aria-label': `Remover a regra ${r.nome}`, onclick: () => gravarAutomacoes(regrasAut.filter((_, k) => k !== i)) }))));
    });
    $('aut-vazio').hidden = ajustarTabela($('aut-tabela'));
  }
  async function gravarAutomacoes(nova) {
    try { regrasAut = await api('PUT', '/api/admin/automacoes', nova); desenharAutomacoes(); $('aut-resultado').textContent = 'Salvo.'; await carregarExecucoes(); }
    catch (e) { if (e.codigo !== 401 && e.codigo !== 403) $('aut-resultado').textContent = e.message; }
  }
  async function carregarExecucoes() {
    const tb = $('aut-exec').tBodies[0];
    const lista = await api('GET', '/api/admin/automacoes/execucoes');
    limpar(tb);
    for (const x of lista.slice(0, 30)) tb.append(el('tr', null, el('td', { class: 'sec', text: dataHora(x.em) }), el('td', { text: x.nome }), el('td', { class: 'sec', text: x.chave })));
    ajustarTabela($('aut-exec'));
  }
  carregadores.automacoes = async () => { regrasAut = await api('GET', '/api/admin/automacoes'); desenharAutomacoes(); await carregarExecucoes(); };
  $('form-aut').addEventListener('submit', (ev) => {
    ev.preventDefault();
    const lista = (id) => $(id).value.split(',').map((x) => x.trim()).filter(Boolean);
    gravarAutomacoes([...regrasAut, {
      id: '', nome: $('aut-nome').value.trim(), ligada: true, gatilho: $('aut-gatilho').value, palavras: lista('aut-palavras'), setores: lista('aut-setores'),
      acao: $('aut-acao').value, texto: $('aut-texto').value.trim(), destino: $('aut-destino').value.trim() || null, responsavel: $('aut-resp').value.trim() || null,
    }]);
  });
})();
